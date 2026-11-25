// ==WindhawkMod==
// @id orbit-evolved-taskbar
// @name Orbit E(volved) Taskbar
// @description Local Codex activity in the Windows 11 taskbar's Widgets area.
// @version 0.1.4
// @author ArmaanLeg3nd
// @github https://github.com/ArmaanLeg3nd/orbit-e
// @include explorer.exe
// @architecture x86-64
// @compilerOptions -lcomctl32 -lole32 -lshell32 -luser32 -lwindowsapp -luuid
// ==/WindhawkMod==

// ==WindhawkModReadme==
/*
# Orbit E(volved) Taskbar
Adds a status button to the primary Windows 11 taskbar, at the Widgets end.
Turn Windows Widgets off and center the taskbar buttons to leave this slot free.
The Orbit E(volved) companion reads
local Codex rollout files and opens the workflow panel when you click.

Set Companion executable to the full path of the taskbar branch's
Orbit E(volved).exe. With an empty setting the mod looks for it in
%LOCALAPPDATA%\OrbitEvolved\Windhawk. The floating release uses a separate
instance and can be closed independently.

Use Settings to adjust the button width and position, show only the headline, or hide
the button when there are no active chats or unread completions.

The taskbar host lookup is adapted from the MIT-licensed
[Taskbar Media Widget by uMk0](https://windhawk.net/mods/taskbar-media-widget).
*/
// ==/WindhawkModReadme==

// ==WindhawkModSettings==
/*
- companionPath: ""
  $name: Companion executable
  $description: Full path to Orbit E(volved).exe built from the orbit-windhawk branch.
- buttonWidth: 144
  $name: Button width
  $description: Logical pixels (96–256). Orbit hides if the chosen width would overlap native buttons.
- horizontalOffset: 4
  $name: Horizontal offset
  $description: Distance from the left edge in logical pixels (0–96).
- verticalOffset: 0
  $name: Vertical offset
  $description: Move up with a negative value or down with a positive value (−16 to 16 logical pixels). The button stays inside the taskbar.
- headlineOnly: false
  $name: Headline only
  $description: Hide the second line. The details remain in the tooltip.
- hideWhenIdle: false
  $name: Hide when idle
  $description: Hide the button when there are no active chats or unread completions.
*/
// ==/WindhawkModSettings==

#include <windhawk_utils.h>
#include <windows.h>
#include <shlobj.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Controls.Primitives.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <string>

using namespace winrt;
using namespace Windows::UI::Xaml;
using namespace Windows::UI::Xaml::Controls;

namespace {
constexpr wchar_t kStatus[] = L"Local\\OrbitEvolved.Taskbar.Status";
constexpr wchar_t kShow[] = L"Local\\OrbitEvolved.Taskbar.Show";
constexpr wchar_t kStop[] = L"Local\\OrbitEvolved.Taskbar.Stop";
constexpr size_t kTextUnits = 256;
constexpr int kDefaultButtonWidth = 144;
constexpr int kDefaultHorizontalOffset = 4;
constexpr double kNativeButtonGap = 4;
constexpr double kButtonHeight = 32;
constexpr UINT_PTR kTimer = 0x4F524254;

void* taskBandSiteVtable;
using GetHost = void* (WINAPI*)(void*, void**);
GetHost getHost;
void* frameHeight;
using ReleaseHost = void (WINAPI*)(void*);
ReleaseHost releaseHost;
HMODULE taskbarModule;
HANDLE stopWorker;
HANDLE worker;
UINT attachMessage;
UINT detachMessage;
std::atomic<bool> stopping;
std::atomic<bool> displaySettingsChanged{true};
int buttonWidth = kDefaultButtonWidth;
int horizontalOffset = kDefaultHorizontalOffset;
int verticalOffset;
bool headlineOnly;
bool hideWhenIdle;
Grid parentGrid{nullptr};
Canvas orbitSlot{nullptr};
Button orbitButton{nullptr};
TextBlock headline{nullptr};
TextBlock caption{nullptr};
ProgressRing activityRing{nullptr};
TextBlock activityDot{nullptr};
event_token clicked{};
ULONGLONG nextLaunch;

bool OwnedTaskbar(HWND hwnd) {
    DWORD process = 0;
    return hwnd && GetWindowThreadProcessId(hwnd, &process) &&
        process == GetCurrentProcessId();
}

bool Readable(void const* ptr, size_t count) {
    MEMORY_BASIC_INFORMATION info{};
    if (!ptr || !VirtualQuery(ptr, &info, sizeof(info)) || info.State != MEM_COMMIT ||
        (info.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    auto offset = reinterpret_cast<uintptr_t>(ptr) - reinterpret_cast<uintptr_t>(info.BaseAddress);
    return offset <= info.RegionSize && count <= info.RegionSize - offset;
}

// Resolve the ABI from the matching Explorer symbols. Unknown layouts fail closed.
XamlRoot FindTaskbarRoot(HWND hwnd) {
    auto bandWindow = reinterpret_cast<HWND>(GetPropW(hwnd, L"TaskbandHWND"));
    if (!OwnedTaskbar(bandWindow)) return nullptr;
    auto slots = reinterpret_cast<void**>(GetWindowLongPtrW(bandWindow, 0));
    void* site = nullptr;
    if (!slots) return nullptr;
    for (unsigned i = 0; i != 20; ++i) {
        if (!Readable(slots + i, sizeof(void*))) break;
        if (slots[i] == taskBandSiteVtable) { site = slots + i; break; }
    }
    auto code = static_cast<unsigned char const*>(frameHeight);
    if (!site || !Readable(code, 8) ||
        code[0] != 0x48 || code[1] != 0x83 || code[2] != 0xEC ||
        code[4] != 0x48 || code[5] != 0x83 || code[6] != 0xC1 ||
        code[7] < 0x10 || code[7] > 0x78 || code[7] % sizeof(void*) != 0) return nullptr;

    struct HostReference {
        void* values[2]{};
        ~HostReference() { if (values[1]) releaseHost(values[1]); }
    } host;
    getHost(site, host.values);
    if (!host.values[0]) return nullptr;
    auto view = reinterpret_cast<::IUnknown**>(static_cast<unsigned char*>(host.values[0]) + code[7]);
    if (!Readable(view, sizeof(*view)) || !Readable(*view, sizeof(void*))) return nullptr;

    FrameworkElement element{nullptr};
    if (FAILED((*view)->QueryInterface(guid_of<FrameworkElement>(), put_abi(element))) || !element)
        return nullptr;
    return element.XamlRoot();
}

FrameworkElement FindElement(DependencyObject const& root, std::wstring_view name, bool byClass = false, int depth = 20) {
    if (!root || depth == 0) return nullptr;
    auto element = root.try_as<FrameworkElement>();
    if (element && (byClass ? get_class_name(element) == name : element.Name() == name))
        return element;
    int count = Media::VisualTreeHelper::GetChildrenCount(root);
    for (int i = 0; i < count; ++i)
        if (auto match = FindElement(Media::VisualTreeHelper::GetChild(root, i), name, byClass, depth - 1))
            return match;
    return nullptr;
}

bool Signal(wchar_t const* name) {
    auto event = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
    if (!event) return false;
    bool sent = SetEvent(event);
    CloseHandle(event);
    return sent;
}

std::wstring CompanionPath() {
    auto setting = Wh_GetStringSetting(L"companionPath");
    std::wstring path = setting ? setting : L"";
    if (setting) Wh_FreeStringSetting(setting);
    if (path.empty()) {
        PWSTR folder = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &folder))) {
            path = folder;
            CoTaskMemFree(folder);
            path += L"\\OrbitEvolved\\Windhawk\\Orbit E(volved).exe";
        }
    }
    auto separator = path.find_last_of(L"\\/");
    if (path.size() < 4 || path[1] != L':' || (path[2] != L'\\' && path[2] != L'/') ||
        path.find(L'"') != std::wstring::npos || separator == std::wstring::npos ||
        _wcsicmp(path.c_str() + separator + 1, L"Orbit E(volved).exe") != 0)
        return {};
    DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY) ? path : L"";
}

void StartCompanion(bool open) {
    if (open && Signal(kShow)) return;
    auto now = GetTickCount64();
    if (!open && now < nextLaunch) return;
    nextLaunch = now + 15000;
    auto path = CompanionPath();
    if (path.empty()) return;
    auto command = L"\"" + path + (open ? L"\" --taskbar-open" : L"\" --taskbar");
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                       nullptr, &startup, &process)) {
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
}

bool ReadStatus(std::wstring& title, std::wstring& detail, wchar_t& activity) {
    auto map = OpenFileMappingW(FILE_MAP_READ, FALSE, kStatus);
    if (!map) return false;
    constexpr size_t bytes = sizeof(LONG) + sizeof(wchar_t) * kTextUnits * 2;
    auto address = static_cast<unsigned char const*>(MapViewOfFile(map, FILE_MAP_READ, 0, 0, bytes));
    bool read = false;
    if (address) {
        auto sequence = reinterpret_cast<LONG const volatile*>(address);
        LONG before = *sequence;
        if (before && !(before & 1)) {
            MemoryBarrier();
            wchar_t fields[kTextUnits * 2];
            memcpy(fields, address + sizeof(LONG), sizeof(fields));
            MemoryBarrier();
            if (before == *sequence) {
                activity = fields[kTextUnits - 1];
                fields[kTextUnits - 1] = fields[kTextUnits * 2 - 1] = L'\0';
                title = fields;
                detail = fields + kTextUnits;
                read = true;
            }
        }
        UnmapViewOfFile(address);
    }
    CloseHandle(map);
    return read;
}

void ShowActivity(wchar_t activity) {
    BOOL systemAnimations = TRUE;
    if (activity == 3)
        SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &systemAnimations, 0);
    bool spin = activity == 3 && systemAnimations;
    bool dot = activity == 2 || (activity == 3 && !systemAnimations);
    if (activityRing.IsActive() != spin) activityRing.IsActive(spin);
    auto ringVisibility = spin ? Visibility::Visible : Visibility::Collapsed;
    auto dotVisibility = dot ? Visibility::Visible : Visibility::Collapsed;
    if (activityRing.Visibility() != ringVisibility) activityRing.Visibility(ringVisibility);
    if (activityDot.Visibility() != dotVisibility) activityDot.Visibility(dotVisibility);
}

void RefreshStatus() {
    std::wstring title, detail;
    wchar_t activity = 0;
    if (!ReadStatus(title, detail, activity)) {
        StartCompanion(false);
        title = L"Orbit";
        detail = L"Open agent activity";
    }
    // This headline is the idle state published by existing Orbit companions.
    if (hideWhenIdle && title == L"No active chats") {
        ShowActivity(0);
        orbitButton.Visibility(Visibility::Collapsed);
        return;
    }
    ShowActivity(activity);
    if (headline.Text() != title) headline.Text(title);
    if (caption.Text() != detail) caption.Text(detail);
    auto accessible = L"Orbit: " + title + (detail.empty() ? L"" : L". " + detail);
    if (Automation::AutomationProperties::GetName(orbitButton) != accessible) {
        Automation::AutomationProperties::SetName(orbitButton, accessible);
        ToolTipService::SetToolTip(orbitButton, box_value(hstring(accessible)));
    }
    orbitButton.Visibility(Visibility::Visible);
}

void Detach() {
    if (orbitButton) orbitButton.Click(clicked);
    if (parentGrid && orbitSlot) {
        uint32_t index;
        auto children = parentGrid.Children();
        if (children.IndexOf(orbitSlot, index)) children.RemoveAt(index);
    }
    orbitButton = nullptr;
    parentGrid = nullptr;
    orbitSlot = nullptr;
    headline = nullptr;
    caption = nullptr;
    activityRing = nullptr;
    activityDot = nullptr;
}

bool HasRoomForOrbit(FrameworkElement const& repeater, double slotWidth) {
    if (orbitSlot.ActualWidth() < slotWidth || orbitSlot.ActualHeight() < kButtonHeight)
        return false;

    int count = Media::VisualTreeHelper::GetChildrenCount(repeater);
    for (int i = 0; i < count; ++i) {
        auto item = Media::VisualTreeHelper::GetChild(repeater, i).try_as<FrameworkElement>();
        if (!item || item.Visibility() != Visibility::Visible ||
            item.ActualWidth() <= 0 || item.ActualHeight() <= 0)
            continue;
        auto bounds = item.TransformToVisual(orbitSlot).TransformBounds(
            {0, 0, static_cast<float>(item.ActualWidth()), static_cast<float>(item.ActualHeight())});
        if (bounds.X < slotWidth && bounds.X + bounds.Width > 0)
            return false;
    }
    return count > 0;
}

void RefreshTaskbar(HWND hwnd) {
    if (displaySettingsChanged.exchange(false)) {
        int width = Wh_GetIntSetting(L"buttonWidth");
        buttonWidth = width == 0 ? kDefaultButtonWidth : std::clamp(width, 96, 256);
        horizontalOffset = std::clamp(Wh_GetIntSetting(L"horizontalOffset"), 0, 96);
        verticalOffset = std::clamp(Wh_GetIntSetting(L"verticalOffset"), -16, 16);
        headlineOnly = Wh_GetIntSetting(L"headlineOnly") != 0;
        hideWhenIdle = Wh_GetIntSetting(L"hideWhenIdle") != 0;
    }
    auto xaml = FindTaskbarRoot(hwnd);
    auto frame = xaml ? FindElement(xaml.Content(), L"Taskbar.TaskbarFrame", true) : nullptr;
    auto repeater = FindElement(frame, L"TaskbarFrameRepeater");
    auto grid = repeater ? Media::VisualTreeHelper::GetParent(repeater).try_as<Grid>() : nullptr;
    RECT bounds{};
    GetClientRect(hwnd, &bounds);
    auto widgets = FindElement(repeater, L"AugmentedEntryPointButton");
    if (!grid || bounds.right <= bounds.bottom ||
        (widgets && widgets.Visibility() == Visibility::Visible && widgets.ActualWidth() > 1)) {
        if (orbitButton) Detach();
        return;
    }
    if (parentGrid != grid) Detach();
    if (!orbitButton) {
        Button button;
        button.Name(L"OrbitEvolvedTaskbarButton");
        button.Height(kButtonHeight);
        button.Padding({6, 0, 6, 0});
        button.MinHeight(0);
        button.HorizontalContentAlignment(HorizontalAlignment::Stretch);
        button.Visibility(Visibility::Collapsed);
        // A Canvas measures its children without contributing their size to the
        // taskbar grid. Its unset background also leaves native hit targets alone.
        Canvas slot;
        Grid::SetColumn(slot, Grid::GetColumn(repeater));
        Grid::SetRow(slot, Grid::GetRow(repeater));
        Grid::SetColumnSpan(slot, Grid::GetColumnSpan(repeater));
        Grid::SetRowSpan(slot, Grid::GetRowSpan(repeater));
        slot.Children().Append(button);
        StackPanel label;
        headline = TextBlock();
        caption = TextBlock();
        headline.FontSize(12);
        caption.FontSize(10);
        caption.Opacity(0.8);
        headline.TextTrimming(TextTrimming::CharacterEllipsis);
        caption.TextTrimming(TextTrimming::CharacterEllipsis);
        label.Children().Append(headline);
        label.Children().Append(caption);
        Grid content;
        ColumnDefinition textColumn;
        ColumnDefinition activityColumn;
        textColumn.Width({1, GridUnitType::Star});
        activityColumn.Width({1, GridUnitType::Auto});
        content.ColumnDefinitions().Append(textColumn);
        content.ColumnDefinitions().Append(activityColumn);
        content.Children().Append(label);
        activityRing = ProgressRing();
        activityRing.Width(16);
        activityRing.Height(16);
        activityRing.MinWidth(0);
        activityRing.MinHeight(0);
        activityRing.Margin({5, 0, 0, 0});
        activityRing.VerticalAlignment(VerticalAlignment::Center);
        activityRing.IsHitTestVisible(false);
        activityRing.Visibility(Visibility::Collapsed);
        Grid::SetColumn(activityRing, 1);
        content.Children().Append(activityRing);
        activityDot = TextBlock();
        activityDot.Text(L"●");
        activityDot.FontSize(9);
        activityDot.Margin({5, 0, 3, 0});
        activityDot.VerticalAlignment(VerticalAlignment::Center);
        activityDot.IsHitTestVisible(false);
        activityDot.Visibility(Visibility::Collapsed);
        Grid::SetColumn(activityDot, 1);
        content.Children().Append(activityDot);
        button.Content(content);
        clicked = button.Click([](auto const&, auto const&) {
            try { StartCompanion(true); } catch (...) { Wh_Log(L"Could not open Orbit"); }
        });
        parentGrid = grid;
        orbitSlot = slot;
        orbitButton = button;
        grid.Children().Append(slot);
        Wh_Log(L"Attached Orbit to the primary taskbar XAML layout");
    }
    double slotWidth = horizontalOffset + buttonWidth + kNativeButtonGap;
    if (!HasRoomForOrbit(repeater, slotWidth)) {
        ShowActivity(0);
        orbitButton.Visibility(Visibility::Collapsed);
        return;
    }
    if (orbitButton.Width() != buttonWidth) orbitButton.Width(buttonWidth);
    auto captionVisibility = headlineOnly ? Visibility::Collapsed : Visibility::Visible;
    if (caption.Visibility() != captionVisibility) caption.Visibility(captionVisibility);
    double verticalSpace = orbitSlot.ActualHeight() - kButtonHeight;
    double top = std::clamp(verticalSpace / 2 + verticalOffset, 0.0, verticalSpace);
    if (Canvas::GetLeft(orbitButton) != horizontalOffset)
        Canvas::SetLeft(orbitButton, horizontalOffset);
    if (Canvas::GetTop(orbitButton) != top)
        Canvas::SetTop(orbitButton, top);
    RefreshStatus();
}

LRESULT CALLBACK TaskbarSubclass(HWND hwnd, UINT message, WPARAM wp, LPARAM lp, DWORD_PTR) {
    if (message == detachMessage || message == WM_NCDESTROY) {
        KillTimer(hwnd, kTimer);
        try { Detach(); } catch (...) { Wh_Log(L"Orbit taskbar detach failed"); }
    } else if (!stopping && (message == attachMessage || (message == WM_TIMER && wp == kTimer))) {
        if (message == attachMessage) SetTimer(hwnd, kTimer, 1000, nullptr);
        try { RefreshTaskbar(hwnd); }
        catch (hresult_error const& error) { Wh_Log(L"Orbit taskbar update failed: 0x%08X", (unsigned)error.code()); }
        catch (...) { Wh_Log(L"Orbit taskbar update failed"); }
        return 0;
    }
    return DefSubclassProc(hwnd, message, wp, lp);
}

DWORD WINAPI WatchTaskbar(void*) {
    HWND attached = nullptr;
    do {
        auto hwnd = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (hwnd != attached && OwnedTaskbar(hwnd) &&
            WindhawkUtils::SetWindowSubclassFromAnyThread(hwnd, TaskbarSubclass, 0)) {
            attached = hwnd;
            PostMessageW(hwnd, attachMessage, 0, 0);
        }
    } while (WaitForSingleObject(stopWorker, 1000) == WAIT_TIMEOUT);
    if (OwnedTaskbar(attached)) {
        SendMessageW(attached, detachMessage, 0, 0);
        WindhawkUtils::RemoveWindowSubclassFromAnyThread(attached, TaskbarSubclass);
    }
    return 0;
}
} // namespace

BOOL Wh_ModInit() {
    if (auto shell = GetShellWindow(); shell && !OwnedTaskbar(shell)) return FALSE;
    taskbarModule = LoadLibraryExW(L"taskbar.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!taskbarModule) return FALSE;
    WindhawkUtils::SYMBOL_HOOK symbols[] = {
        {{LR"(const CTaskBand::`vftable'{for `ITaskListWndSite'})"}, &taskBandSiteVtable},
        {{LR"(public: virtual class std::shared_ptr<class TaskbarHost> __cdecl CTaskBand::GetTaskbarHost(void)const )"}, &getHost},
        {{LR"(public: int __cdecl TaskbarHost::FrameHeight(void)const )"}, &frameHeight},
        {{LR"(public: void __cdecl std::_Ref_count_base::_Decref(void))"}, &releaseHost},
    };
    if (!WindhawkUtils::HookSymbols(taskbarModule, symbols, ARRAYSIZE(symbols))) {
        FreeLibrary(taskbarModule);
        return FALSE;
    }
    attachMessage = RegisterWindowMessageW(L"OrbitEvolved_Taskbar_Attach");
    detachMessage = RegisterWindowMessageW(L"OrbitEvolved_Taskbar_Detach");
    stopWorker = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stopWorker || !attachMessage || !detachMessage) {
        if (stopWorker) CloseHandle(stopWorker);
        FreeLibrary(taskbarModule);
        return FALSE;
    }
    return TRUE;
}

void Wh_ModAfterInit() {
    worker = CreateThread(nullptr, 0, WatchTaskbar, nullptr, 0, nullptr);
}

void Wh_ModSettingsChanged() {
    displaySettingsChanged = true;
}

void Wh_ModUninit() {
    stopping = true;
    SetEvent(stopWorker);
    if (worker) {
        WaitForSingleObject(worker, INFINITE);
        CloseHandle(worker);
    }
    CloseHandle(stopWorker);
    Signal(kStop);
    FreeLibrary(taskbarModule);
}
