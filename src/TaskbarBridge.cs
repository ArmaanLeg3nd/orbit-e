using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace OrbitEvolved
{
    internal sealed class TaskbarBridge : IDisposable
    {
        public const string Instance = @"Local\OrbitEvolved.Taskbar";
        private const int TextUnits = 256;
        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor view;
        private readonly EventWaitHandle stop;
        private int revision;
        private string previousTitle, previousSubtitle;
        private bool previousWorking, previousAnimations;

        public TaskbarBridge(string instance = Instance)
        {
            map = MemoryMappedFile.CreateOrOpen(instance + ".Status", 4 + TextUnits * 4);
            view = map.CreateViewAccessor();
            stop = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Stop");
            Publish("Orbit", "Reading local Codex activity");
        }

        public bool StopRequested { get { return stop.WaitOne(0); } }

        public void SetAnimations(bool enabled)
        {
            Publish(previousTitle, previousSubtitle, previousWorking, enabled);
        }

        public void Publish(string title, string subtitle, bool working = false, bool animate = true)
        {
            title = Json.Clean(title, TextUnits - 1);
            subtitle = Json.Clean(subtitle, TextUnits - 1);
            if (title == previousTitle && subtitle == previousSubtitle &&
                working == previousWorking && animate == previousAnimations) return;
            var fields = new ushort[TextUnits * 2];
            for (var i = 0; i < title.Length; i++) fields[i] = title[i];
            for (var i = 0; i < subtitle.Length; i++) fields[TextUnits + i] = subtitle[i];
            // Older readers already terminate the title at this reserved code unit.
            fields[TextUnits - 1] = working ? (ushort)(animate ? 3 : 2) : (ushort)1;
            view.Write(0, unchecked(++revision));
            Thread.MemoryBarrier();
            view.WriteArray(4, fields, 0, fields.Length);
            Thread.MemoryBarrier();
            view.Write(0, unchecked(++revision));
            previousTitle = title;
            previousSubtitle = subtitle;
            previousWorking = working;
            previousAnimations = animate;
        }

        public void Dispose()
        {
            stop.Dispose();
            view.Dispose();
            map.Dispose();
        }
    }
}
