// Copyright (c) 2026 Mansur
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;

namespace Mansur.Next.Desktop
{
    // UI-thread/broker-lock state. No clock, model or window access in this policy.
    internal sealed class LearningWindowLifetime
    {
        internal static readonly long ReadingDelay = Stopwatch.Frequency * 3L;
        private long? completedAt;
        internal void Reset() { completedAt = null; }
        internal bool ShouldHide(bool synthesisFinished, bool playbackDrained, bool pointerInside, long now)
        {
            if (!synthesisFinished || !playbackDrained || pointerInside) { Reset(); return false; }
            if (!completedAt.HasValue || now < completedAt.Value) { completedAt = now; return false; }
            return now - completedAt.Value >= ReadingDelay;
        }
    }
    internal static class LearningWindowLifetimeTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var lifetime = new LearningWindowLifetime();
            long delay = LearningWindowLifetime.ReadingDelay;
            check(!lifetime.ShouldHide(false, true, false, 0) && !lifetime.ShouldHide(false, true, false, delay * 10),
                "learning-window-never-times-out-during-synthesis-gap");
            check(!lifetime.ShouldHide(true, false, false, delay * 11) && !lifetime.ShouldHide(true, false, false, delay * 20),
                "learning-window-keeps-long-audio-visible-after-synthesis-done");
            check(!lifetime.ShouldHide(true, true, false, delay * 21) && !lifetime.ShouldHide(true, true, false, delay * 22 - 1) &&
                lifetime.ShouldHide(true, true, false, delay * 22), "learning-window-hides-three-seconds-after-playback-drains");
            check(!lifetime.ShouldHide(true, true, true, delay * 30) && !lifetime.ShouldHide(true, true, true, delay * 50) &&
                !lifetime.ShouldHide(true, true, false, delay * 51) && !lifetime.ShouldHide(true, true, false, delay * 52 - 1) &&
                lifetime.ShouldHide(true, true, false, delay * 52), "learning-window-hover-preserves-reading-then-full-delay-on-leave");
            lifetime.Reset();
            check(!lifetime.ShouldHide(true, true, false, delay * 60), "learning-window-replay-or-new-request-does-not-inherit-old-deadline");
            check(!lifetime.ShouldHide(true, false, false, delay * 61) && !lifetime.ShouldHide(true, true, false, delay * 65),
                "learning-window-new-audio-clears-previous-drained-timer");
        }
    }
}
