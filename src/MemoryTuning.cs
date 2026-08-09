using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TodoWall
{
    /// <summary>
    /// This app spends almost all of its life idle on the desktop, so it is worth
    /// handing memory back rather than sitting on it. Trim() is called after the
    /// bursty moments (startup, building the blurred backdrop) and occasionally
    /// while idle - never mid-interaction, where the page faults would be felt.
    /// </summary>
    internal static class MemoryTuning
    {
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>GetCurrentProcess() returns this pseudo-handle rather than a real one.
        /// It needs no opening and no closing, so the trim path can skip constructing a
        /// Process just to reach a handle it hands straight back.</summary>
        static readonly IntPtr CurrentProcess = new IntPtr(-1);

        public static void Trim() { Trim(true); }

        /// <param name="releasePages">Also push the working set out to the standby list.
        /// Only worth doing when the app is genuinely idle. Every page evicted is a fault
        /// the next interaction has to pay back, which on a slow disk is exactly the stutter
        /// this was meant to avoid - and it is what makes Task Manager read ~15MB right
        /// after a trim and ~30MB again the moment you touch anything. Neither number is a
        /// leak; the low one is just an emptied working set.</param>
        public static void Trim(bool releasePages)
        {
            try
            {
                // Compacting a gen2 blocks every thread while it moves objects. The point
                // here is only to let go of the blur's temporaries, which a plain blocking
                // collect already does - the compaction was paying a stall for tidiness.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, false);

                if (releasePages)
                    EmptyWorkingSet(CurrentProcess);
            }
            catch { }
        }

        public static long WorkingSetMB
        {
            get
            {
                try
                {
                    using (Process me = Process.GetCurrentProcess())
                        return me.WorkingSet64 / (1024 * 1024);
                }
                catch { return 0; }
            }
        }
    }
}
