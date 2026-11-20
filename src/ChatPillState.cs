using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbitEvolved
{
    // Keep a displayed completion readable even after it ages out of the Active list.
    internal sealed class ChatPillState
    {
        private readonly Dictionary<string, DateTime> acknowledged = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private DateTime dismissedThrough;

        public ActiveChatEntry Target { get; private set; }

        public bool IsAcknowledged(ActiveChatEntry entry)
        {
            DateTime started;
            return entry.Activity == ChatActivity.Done && acknowledged.TryGetValue(entry.Chat.Id, out started) &&
                entry.StartedAt <= started;
        }

        public bool Acknowledge(ActiveChatEntry entry)
        {
            if (entry == null || entry.Activity != ChatActivity.Done) return false;
            if (!IsAcknowledged(entry)) acknowledged[entry.Chat.Id] = entry.StartedAt;
            DismissThrough(entry.CompletedAt);
            if (Target != null && Target.Activity == ChatActivity.Done && IsAcknowledged(Target))
                Target = null;
            return true;
        }

        public void Observe(Snapshot snapshot)
        {
            var entries = snapshot.ActiveChatEntries;
            var live = entries.FirstOrDefault(entry => entry.Activity != ChatActivity.Done);
            var previous = Target == null || Target.Activity != ChatActivity.Done ? null : Target;

            if (live != null)
            {
                if (previous != null)
                {
                    // Work that needs an update supersedes the completion already shown.
                    Acknowledge(previous);
                }
                Target = live;
                return;
            }

            if (previous != null && !snapshot.Chats.Any(chat => chat.Id == previous.Chat.Id && !chat.Archived))
            {
                DismissThrough(previous.CompletedAt);
                previous = null;
            }

            var latest = entries.FirstOrDefault(entry => entry.Activity == ChatActivity.Done &&
                entry.CompletedAt > dismissedThrough && !IsAcknowledged(entry));
            Target = latest != null && (previous == null || latest.CompletedAt >= previous.CompletedAt) ? latest : previous;
        }

        private void DismissThrough(DateTime completed)
        {
            if (completed > dismissedThrough) dismissedThrough = completed;
        }
    }
}
