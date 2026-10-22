using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Discphew.Native;

namespace Discphew.Disc
{
    /// <summary>Late-bound helpers for IDispatch objects. IMAPI2 is fully scriptable, so this is all we need.</summary>
    static class Com
    {
        public static object Create(string progId)
        {
            var t = Type.GetTypeFromProgID(progId, false) ?? throw new DiscException($"Windows' disc burning service ({progId}) isn't available.");
            return Activator.CreateInstance(t);
        }

        public static object Get(object o, string name, params object[] args) => Wrap(() => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args));
        public static void Set(object o, string name, object value) => Wrap(() => o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value }));
        public static object Call(object o, string name, params object[] args) => Wrap(() => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args));

        public static T GetOr<T>(object o, string name, T fallback)
        {
            try { return (T)Convert.ChangeType(Get(o, name), typeof(T)); }
            catch { return fallback; }
        }

        static object Wrap(Func<object> f)
        {
            try { return f(); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    // ---- IMAPI2 event interfaces (dispatch-only) ----

    [ComImport, Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface DDiscFormat2DataEvents
    {
        [DispId(0x200)] void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ComImport, Guid("2735413F-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface DDiscFormat2TrackAtOnceEvents
    {
        [DispId(0x200)] void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ComImport, Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface DDiscFormat2EraseEvents
    {
        [DispId(0x200)] void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, int elapsedSeconds, int estimatedTotalSeconds);
    }

    [ComImport, Guid("27354142-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface DDiscFormat2RawCDEvents
    {
        [DispId(0x200)] void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
    }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    public sealed class WriteEventSink : DDiscFormat2DataEvents, DDiscFormat2TrackAtOnceEvents, DDiscFormat2RawCDEvents
    {
        public Action<object> OnUpdate;
        void DDiscFormat2DataEvents.Update(object sender, object progress) => OnUpdate?.Invoke(progress);
        void DDiscFormat2TrackAtOnceEvents.Update(object sender, object progress) => OnUpdate?.Invoke(progress);
        void DDiscFormat2RawCDEvents.Update(object sender, object progress) => OnUpdate?.Invoke(progress);
    }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    public sealed class EraseEventSink : DDiscFormat2EraseEvents
    {
        public Action<int, int> OnUpdate;
        public void Update(object sender, int elapsedSeconds, int estimatedTotalSeconds) => OnUpdate?.Invoke(elapsedSeconds, estimatedTotalSeconds);
    }

    public sealed class BurnProgress
    {
        public string Phase = "";
        /// <summary>0..1, or negative when the step has no measurable progress.</summary>
        public double Fraction = -1;
        public int ElapsedSeconds;
        public int RemainingSeconds = -1;
    }

    public sealed class BurnOptions
    {
        public char Drive;
        /// <summary>Sectors per second, 0 = let the drive pick its maximum.</summary>
        public int Speed;
        public bool CloseDisc = true;
        public bool EjectWhenDone = true;
        public Action<BurnProgress> Progress;
    }

    public enum AudioBurnMode { TrackAtOnce, DiscAtOnce, DiscAtOnceWithCdText }

    public sealed class WriteSpeed
    {
        public int SectorsPerSecond;
        public string Label = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// Burning, erasing and media inspection through IMAPI2, the disc mastering API built into Windows.
    /// Every method here is synchronous and must run on an MTA (thread-pool) thread.
    /// </summary>
    public static class Imapi
    {
        const string ClientName = "discphew";
        static readonly Guid DataEventsIid = typeof(DDiscFormat2DataEvents).GUID;
        static readonly Guid TaoEventsIid = typeof(DDiscFormat2TrackAtOnceEvents).GUID;
        static readonly Guid RawEventsIid = typeof(DDiscFormat2RawCDEvents).GUID;
        static readonly Guid EraseEventsIid = typeof(DDiscFormat2EraseEvents).GUID;

        static object activeWriter;
        static string activeCancelMethod;

        public static object FindRecorder(char letter)
        {
            var master = Com.Create("IMAPI2.MsftDiscMaster2");
            try
            {
                if (!(bool)Com.Get(master, "IsSupportedEnvironment")) return null;
                int count = (int)Com.Get(master, "Count");
                for (int i = 0; i < count; i++)
                {
                    string id = (string)Com.Get(master, "Item", i);
                    var rec = Com.Create("IMAPI2.MsftDiscRecorder2");
                    Com.Call(rec, "InitializeDiscRecorder", id);
                    if (Com.Get(rec, "VolumePathNames") is object[] paths &&
                        paths.OfType<string>().Any(p => p.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase)))
                        return rec;
                    Com.Release(rec);
                }
                return null;
            }
            finally { Com.Release(master); }
        }

        static MediaKind MapPhysical(int t) => t switch
        {
            1 => MediaKind.CdRom, 2 => MediaKind.CdR, 3 => MediaKind.CdRw,
            4 => MediaKind.DvdRom, 5 => MediaKind.DvdRam, 6 => MediaKind.DvdPlusR, 7 => MediaKind.DvdPlusRw,
            8 => MediaKind.DvdPlusRDl, 9 => MediaKind.DvdDashR, 10 => MediaKind.DvdDashRw, 11 => MediaKind.DvdDashRDl,
            13 => MediaKind.DvdPlusRwDl, 17 => MediaKind.BdRom, 18 => MediaKind.BdR, 19 => MediaKind.BdRe,
            0 => MediaKind.Unknown, _ => MediaKind.Other,
        };

        public static MediaInfo QueryMedia(char letter)
        {
            var rec = FindRecorder(letter);
            if (rec == null) return null;
            var fmt = Com.Create("IMAPI2.MsftDiscFormat2Data");
            try
            {
                var info = new MediaInfo
                {
                    RecorderName = ($"{Com.GetOr(rec, "VendorId", "")} {Com.GetOr(rec, "ProductId", "")}").Trim(),
                    RecorderId = Com.GetOr(rec, "ActiveDiscRecorder", ""),
                };
                if (Com.Get(rec, "SupportedProfiles") is object[] profiles)
                {
                    var p = profiles.Select(x => Convert.ToInt32(x)).ToList();
                    info.CanBurnCd = p.Contains(0x09) || p.Contains(0x0A);
                    info.CanBurnDvd = p.Any(x => x == 0x11 || x == 0x13 || x == 0x14 || x == 0x1A || x == 0x1B || x == 0x2B || x == 0x15 || x == 0x16);
                }
                if (!(bool)Com.Call(fmt, "IsRecorderSupported", rec)) return info;
                Com.Set(fmt, "Recorder", rec);
                try { info.Kind = MapPhysical(Convert.ToInt32(Com.Get(fmt, "CurrentPhysicalMediaType"))); }
                catch { return info; }

                info.Rewritable = info.Kind is MediaKind.CdRw or MediaKind.DvdPlusRw or MediaKind.DvdDashRw or MediaKind.DvdRam or MediaKind.BdRe or MediaKind.DvdPlusRwDl;
                if ((bool)Com.Call(fmt, "IsCurrentMediaSupported", rec))
                {
                    int state = Convert.ToInt32(Com.Get(fmt, "CurrentMediaStatus"));
                    info.Writable = (state & 0xFC00) == 0 && (state & 0x4000) == 0;
                    info.Blank = Com.GetOr(fmt, "MediaHeuristicallyBlank", false);
                    info.Appendable = (state & 0x4) != 0;
                    info.TotalSectors = Com.GetOr(fmt, "TotalSectorsOnMedia", 0L);
                    info.FreeSectors = Com.GetOr(fmt, "FreeSectorsOnMedia", 0L);
                }
                return info;
            }
            finally { Com.Release(fmt); Com.Release(rec); }
        }

        public static List<WriteSpeed> QuerySpeeds(char letter, bool audio)
        {
            var list = new List<WriteSpeed>();
            var rec = FindRecorder(letter);
            if (rec == null) return list;
            var fmt = Com.Create(audio ? "IMAPI2.MsftDiscFormat2TrackAtOnce" : "IMAPI2.MsftDiscFormat2Data");
            try
            {
                Com.Set(fmt, "Recorder", rec);
                if (Com.Get(fmt, "SupportedWriteSpeeds") is object[] speeds)
                {
                    int type = 0;
                    try { type = Convert.ToInt32(Com.Get(fmt, "CurrentPhysicalMediaType")); } catch { }
                    double unit = type >= 4 ? 676.27 : 75.0;   // sectors per second at 1x
                    foreach (var s in speeds.Select(x => Convert.ToInt32(x)).Distinct().OrderByDescending(x => x))
                        list.Add(new WriteSpeed { SectorsPerSecond = s, Label = $"{s / unit:0.#}×" });
                }
            }
            catch { }
            finally { Com.Release(fmt); Com.Release(rec); }
            return list;
        }

        public static void Cancel()
        {
            var w = activeWriter;
            if (w == null) return;
            try { Com.Call(w, activeCancelMethod); } catch { }
        }

        static object RequireRecorder(char letter) =>
            FindRecorder(letter) ?? throw new DiscException($"Drive {letter}: isn't a recorder Windows can burn with.");

        static void SetSpeed(object fmt, int speed)
        {
            try { Com.Call(fmt, "SetWriteSpeed", speed <= 0 ? -1 : speed, false); } catch { }
        }

        static int Advise(object source, Guid iid, object sink, out IConnectionPoint cp)
        {
            var cpc = (IConnectionPointContainer)source;
            cpc.FindConnectionPoint(ref iid, out cp);
            cp.Advise(sink, out int cookie);
            return cookie;
        }

        static string DataPhase(int action) => action switch
        {
            0 => "Checking the disc",
            1 => "Formatting the disc",
            2 => "Preparing the drive",
            3 => "Calibrating laser power",
            4 => "Writing",
            5 => "Finalizing",
            6 => "Done",
            7 => "Verifying",
            _ => "Working",
        };

        static void ReportWrite(BurnOptions o, object args, int trackIndex, int trackCount)
        {
            if (o.Progress == null) return;
            var p = new BurnProgress
            {
                ElapsedSeconds = Com.GetOr(args, "ElapsedTime", 0),
                RemainingSeconds = Com.GetOr(args, "RemainingTime", -1),
            };
            int action = Com.GetOr(args, "CurrentAction", -1);
            long start = Com.GetOr(args, "StartLba", 0L), count = Com.GetOr(args, "SectorCount", 0L), last = Com.GetOr(args, "LastWrittenLba", 0L);
            double frac = count > 0 ? Math.Max(0, Math.Min(1, (last - start) / (double)count)) : -1;

            if (trackCount > 0)
            {
                // Track-at-once: 0 unknown, 1 preparing, 2 writing, 3 finishing, 4 verifying
                p.Phase = action switch { 1 => "Preparing", 2 => $"Writing track {trackIndex + 1} of {trackCount}", 3 => "Finishing", 4 => "Verifying", _ => "Working" };
                p.Fraction = action == 2 && frac >= 0 ? (trackIndex + frac) / trackCount : -1;
            }
            else
            {
                p.Phase = DataPhase(action);
                p.Fraction = action == 4 ? frac : action == 6 ? 1 : -1;
            }
            o.Progress(p);
        }

        /// <summary>Raw disc-at-once progress. <paramref name="starts"/> holds each track's first LBA, then the lead-out's.</summary>
        static void ReportRaw(BurnOptions o, object args, long[] starts, System.Diagnostics.Stopwatch clock)
        {
            if (o.Progress == null) return;
            int action = Com.GetOr(args, "CurrentAction", -1);
            long start = Com.GetOr(args, "StartLba", 0L), count = Com.GetOr(args, "SectorCount", 0L), last = Com.GetOr(args, "LastWrittenLba", 0L);
            int tracks = starts.Length - 1, track = starts.Take(tracks).Count(s => s <= last);
            string writing = last < 0 ? "Writing the lead-in" : last >= starts[tracks] ? "Writing the lead-out" : $"Writing track {track} of {tracks}";
            double frac = action == 2 && count > 0 ? Math.Max(0, Math.Min(1, (last - start) / (double)count)) : -1;
            if (action == 2 && !clock.IsRunning) clock.Start();
            double elapsed = clock.Elapsed.TotalSeconds;
            o.Progress(new BurnProgress
            {
                // Raw CD: 0 unknown, 1 preparing, 2 writing, 3 finishing
                Phase = action switch { 1 => "Preparing", 2 => writing, 3 => "Finishing", _ => "Working" },
                Fraction = frac,
                ElapsedSeconds = (int)elapsed,
                // The raw writer's own RemainingTime never moves, so estimate it from the rate so far.
                RemainingSeconds = frac > 0.03 && elapsed > 5 ? (int)(elapsed * (1 - frac) / frac) : -1,
            });
        }

        static void Finish(BurnOptions o, object rec)
        {
            o.Progress?.Invoke(new BurnProgress { Phase = "Done", Fraction = 1 });
            if (o.EjectWhenDone)
                try { Com.Call(rec, "EjectMedia"); } catch { }
        }

        /// <summary>Burns files and folders as a new session (ISO 9660 + Joliet + UDF).</summary>
        public static void BurnData(BurnOptions o, string volumeLabel, IList<string> paths)
        {
            var rec = RequireRecorder(o.Drive);
            var fmt = Com.Create("IMAPI2.MsftDiscFormat2Data");
            var fsi = Com.Create("IMAPI2FS.MsftFileSystemImage");
            var streams = new List<IStream>();
            object image = null, imageStream = null;
            IConnectionPoint cp = null; int cookie = 0;
            try
            {
                Com.Set(fmt, "Recorder", rec);
                Com.Set(fmt, "ClientName", ClientName);
                if (!(bool)Com.Call(fmt, "IsCurrentMediaSupported", rec))
                    throw new DiscException("Insert a blank or appendable writable disc (CD-R/RW, DVD±R/RW).");
                Com.Set(fmt, "ForceMediaToBeClosed", o.CloseDisc);
                SetSpeed(fmt, o.Speed);

                o.Progress?.Invoke(new BurnProgress { Phase = "Building the file system" });
                Com.Call(fsi, "ChooseImageDefaults", rec);
                Com.Set(fsi, "FileSystemsToCreate", 1 | 2 | 4);
                string label = string.IsNullOrWhiteSpace(volumeLabel) ? DateTime.Now.ToString("yyyy-MM-dd") : volumeLabel.Trim();
                Com.Set(fsi, "VolumeName", label.Length > 16 ? label.Substring(0, 16) : label);

                if (!Com.GetOr(fmt, "MediaHeuristicallyBlank", true))
                {
                    Com.Set(fsi, "MultisessionInterfaces", Com.Get(fmt, "MultisessionInterfaces"));
                    Com.Call(fsi, "ImportFileSystem");
                }

                var root = Com.Get(fsi, "Root");
                foreach (var path in paths)
                {
                    if (Directory.Exists(path))
                        Com.Call(root, "AddTree", path, true);
                    else
                    {
                        var s = OpenStream(path);
                        streams.Add(s);
                        Com.Call(root, "AddFile", Path.GetFileName(path), s);
                    }
                }

                image = Com.Call(fsi, "CreateResultImage");
                imageStream = Com.Get(image, "ImageStream");

                var sink = new WriteEventSink { OnUpdate = a => ReportWrite(o, a, 0, 0) };
                cookie = Advise(fmt, DataEventsIid, sink, out cp);
                activeWriter = fmt; activeCancelMethod = "CancelWrite";
                Com.Call(fmt, "Write", imageStream);
                Finish(o, rec);
            }
            catch (COMException e) { throw Translate(e); }
            finally
            {
                activeWriter = null;
                if (cp != null) try { cp.Unadvise(cookie); } catch { }
                Com.Release(imageStream); Com.Release(image);
                foreach (var s in streams) Com.Release(s);
                Com.Release(fsi); Com.Release(fmt); Com.Release(rec);
            }
        }

        /// <summary>Burns an ISO (2048-byte sector) image as-is.</summary>
        public static void BurnImage(BurnOptions o, string isoPath)
        {
            if (new FileInfo(isoPath).Length % 2048 != 0)
                throw new DiscException("That image isn't made of 2048-byte sectors. Only ISO-style images can be burned here.");
            var rec = RequireRecorder(o.Drive);
            var fmt = Com.Create("IMAPI2.MsftDiscFormat2Data");
            IStream stream = null;
            IConnectionPoint cp = null; int cookie = 0;
            try
            {
                Com.Set(fmt, "Recorder", rec);
                Com.Set(fmt, "ClientName", ClientName);
                if (!(bool)Com.Call(fmt, "IsCurrentMediaSupported", rec))
                    throw new DiscException("Insert a blank writable disc.");
                Com.Set(fmt, "ForceMediaToBeClosed", o.CloseDisc);
                SetSpeed(fmt, o.Speed);
                stream = OpenStream(isoPath);
                var sink = new WriteEventSink { OnUpdate = a => ReportWrite(o, a, 0, 0) };
                cookie = Advise(fmt, DataEventsIid, sink, out cp);
                activeWriter = fmt; activeCancelMethod = "CancelWrite";
                Com.Call(fmt, "Write", stream);
                Finish(o, rec);
            }
            catch (COMException e) { throw Translate(e); }
            finally
            {
                activeWriter = null;
                if (cp != null) try { cp.Unadvise(cookie); } catch { }
                Com.Release(stream); Com.Release(fmt); Com.Release(rec);
            }
        }

        /// <summary>
        /// Burns an audio CD. Each file must be raw 44.1 kHz / 16-bit / stereo little-endian PCM whose
        /// length is a multiple of 2352 bytes. A blank disc that's being closed goes on disc-at-once
        /// (gapless, with <paramref name="cdText"/> packs in the lead-in if the drive takes raw subcode);
        /// otherwise, or if the drive can't write raw, the tracks are written track-at-once.
        /// </summary>
        public static AudioBurnMode BurnAudio(BurnOptions o, IList<string> pcmFiles, byte[] cdText = null)
        {
            if (o.CloseDisc && TryBurnAudioRaw(o, pcmFiles, cdText) is AudioBurnMode mode) return mode;
            BurnAudioTrackAtOnce(o, pcmFiles);
            return AudioBurnMode.TrackAtOnce;
        }

        internal const int SubcodePqOnly = 1, SubcodeRaw = 3;
        internal const int RawImageStartLba = -22650;        // raw images start at MSF 95:00:00, inside any disc's lead-in
        const int FirstPregap = 150;

        /// <summary>Disc-at-once through IMAPI's raw (DAO-96) writer. Returns null, having written nothing, when the drive or disc can't do it.</summary>
        static AudioBurnMode? TryBurnAudioRaw(BurnOptions o, IList<string> pcmFiles, byte[] cdText)
        {
            if (Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2RawCD", false) == null) return null;
            var rec = RequireRecorder(o.Drive);
            var raw = Com.Create("IMAPI2.MsftDiscFormat2RawCD");
            var streams = new List<IStream>();
            object creator = null, image = null;
            IStream subcode = null;
            IConnectionPoint cp = null; int cookie = 0;
            bool prepared = false;
            string step = "checking the drive and disc";
            try
            {
                if (!(bool)Com.Call(raw, "IsRecorderSupported", rec)) return null;
                Com.Set(raw, "Recorder", rec);
                Com.Set(raw, "ClientName", ClientName);
                if (!(bool)Com.Call(raw, "IsCurrentMediaSupported", rec) || !Com.GetOr(raw, "MediaHeuristicallyBlank", false)) return null;
                var types = Com.Get(raw, "SupportedSectorTypes") is object[] t ? t.Select(x => Convert.ToInt32(x)).ToList() : new List<int>();
                // CD-Text needs raw R-W subcode: in the lead-in its packs are written as they are, not interleaved.
                bool withText = cdText != null && cdText.Length > 0 && types.Contains(SubcodeRaw);
                int type = withText ? SubcodeRaw : types.Contains(SubcodePqOnly) ? SubcodePqOnly : types.FirstOrDefault();
                if (type == 0) return null;

                var starts = new long[pcmFiles.Count + 1];
                for (int i = 0; i < pcmFiles.Count; i++) starts[i + 1] = starts[i] + new FileInfo(pcmFiles[i]).Length / 2352;

                step = "laying out the disc image";
                o.Progress?.Invoke(new BurnProgress { Phase = "Laying out the disc" });
                if (withText) subcode = CdTextSubcode(pcmFiles, cdText, streams);
                creator = CreateRawImage(type, pcmFiles, subcode, streams, out image);
                SetSpeed(raw, o.Speed);

                var clock = new System.Diagnostics.Stopwatch();
                var sink = new WriteEventSink { OnUpdate = a => ReportRaw(o, a, starts, clock) };
                cookie = Advise(raw, RawEventsIid, sink, out cp);
                activeWriter = raw; activeCancelMethod = "CancelWrite";

                // Most of the raw writer's properties only answer once the disc is prepared (locked for this burn).
                step = "preparing the disc";
                o.Progress?.Invoke(new BurnProgress { Phase = "Preparing the disc" });
                Com.Call(raw, "PrepareMedia");
                prepared = true;
                step = "choosing the write mode";
                Com.Set(raw, "RequestedSectorType", type);
                step = "measuring the disc";
                long limit = Com.GetOr(raw, "LastPossibleStartOfLeadout", 0L);
                if (limit > 0 && starts[pcmFiles.Count] > limit) throw new DiscException($"Those tracks need {Fmt.Time(TimeSpan.FromSeconds(starts[pcmFiles.Count] / 75.0))} but the disc only has {Fmt.Time(TimeSpan.FromSeconds(limit / 75.0))}.");
                step = "writing";
                Com.Call(raw, "WriteMedia", image);
                step = "closing the disc";
                o.Progress?.Invoke(new BurnProgress { Phase = "Closing the disc" });
                Com.Call(raw, "ReleaseMedia");
                prepared = false;
                Finish(o, rec);
                return withText ? AudioBurnMode.DiscAtOnceWithCdText : AudioBurnMode.DiscAtOnce;
            }
            catch (COMException e) { throw Translate(e, step); }
            finally
            {
                activeWriter = null;
                if (prepared) try { Com.Call(raw, "ReleaseMedia"); } catch { }
                if (cp != null) try { cp.Unadvise(cookie); } catch { }
                Com.Release(image); Com.Release(creator); Com.Release(subcode);
                foreach (var s in streams) Com.Release(s);
                Com.Release(raw); Com.Release(rec);
            }
        }

        /// <summary>
        /// R-W subcode carrying the CD-Text packs, written next to the first track as cdtext.sub.
        /// The stream has to cover the raw image sector for sector, so the image is laid out once to measure it.
        /// </summary>
        internal static IStream CdTextSubcode(IList<string> pcmFiles, byte[] cdText, List<IStream> streams)
        {
            var probe = CreateRawImage(SubcodeRaw, pcmFiles, null, streams, out var probeImage);
            ((IStream)probeImage).Stat(out var stat, 1);
            Com.Release(probeImage); Com.Release(probe);
            string path = Path.Combine(Path.GetDirectoryName(pcmFiles[0]), "cdtext.sub");
            // CD-Text repeats through the lead-in and stops a second short of track 1's pregap, which
            // keeps the program area's R-W clear (IMAPI reads the stream a couple of sectors ahead).
            WriteLeadInSubcode(path, CdText.ToSubcode(cdText), stat.cbSize / 2448, -RawImageStartLba - FirstPregap - 75);
            return OpenStream(path);
        }

        /// <summary>Lays the tracks out gapless (track 1 gets the standard 2-second pregap) as a raw image starting at MSF 95:00:00.</summary>
        internal static object CreateRawImage(int type, IList<string> pcmFiles, IStream subcode, List<IStream> streams, out object image)
        {
            var creator = Com.Create("IMAPI2.MsftRawCDImageCreator");
            Com.Set(creator, "ResultingImageType", type);
            foreach (var f in pcmFiles)
            {
                var s = OpenStream(f);
                streams.Add(s);
                Com.Call(creator, "AddTrack", 0, s);       // IMAPI_CD_SECTOR_AUDIO
            }
            if (subcode != null) Com.Call(creator, "AddSubcodeRWGenerator", subcode);
            image = Com.Call(creator, "CreateResultImage");
            return creator;
        }

        /// <summary>Writes 96 bytes of R-W subcode per image sector: the CD-Text cycle for the first <paramref name="textSectors"/>, silence after.</summary>
        static void WriteLeadInSubcode(string path, byte[] symbols, long sectors, long textSectors)
        {
            var sector = new byte[96];
            int at = 0;
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            for (long s = 0; s < sectors; s++)
            {
                if (s < textSectors)
                    for (int i = 0; i < 96; i++) { sector[i] = symbols[at++]; if (at == symbols.Length) at = 0; }
                else if (s == textSectors)
                    Array.Clear(sector, 0, 96);
                fs.Write(sector, 0, 96);
            }
        }

        static void BurnAudioTrackAtOnce(BurnOptions o, IList<string> pcmFiles)
        {
            var rec = RequireRecorder(o.Drive);
            var tao = Com.Create("IMAPI2.MsftDiscFormat2TrackAtOnce");
            IConnectionPoint cp = null; int cookie = 0;
            bool prepared = false;
            try
            {
                Com.Set(tao, "Recorder", rec);
                Com.Set(tao, "ClientName", ClientName);
                if (!(bool)Com.Call(tao, "IsCurrentMediaSupported", rec))
                    throw new DiscException("Audio CDs need a blank CD-R or CD-RW.");
                long needed = pcmFiles.Sum(f => new FileInfo(f).Length / 2352);
                long free = Com.GetOr(tao, "FreeSectorsOnMedia", long.MaxValue);
                if (needed > free) throw new DiscException($"Those tracks need {Fmt.Time(TimeSpan.FromSeconds(needed / 75.0))} but the disc only has {Fmt.Time(TimeSpan.FromSeconds(free / 75.0))}.");
                Com.Set(tao, "DoNotFinalizeMedia", !o.CloseDisc);
                SetSpeed(tao, o.Speed);

                int index = 0;
                var sink = new WriteEventSink { OnUpdate = a => ReportWrite(o, a, index, pcmFiles.Count) };
                cookie = Advise(tao, TaoEventsIid, sink, out cp);
                activeWriter = tao; activeCancelMethod = "CancelAddTrack";

                o.Progress?.Invoke(new BurnProgress { Phase = "Preparing the disc" });
                Com.Call(tao, "PrepareMedia");
                prepared = true;
                for (index = 0; index < pcmFiles.Count; index++)
                {
                    var s = OpenStream(pcmFiles[index]);
                    try { Com.Call(tao, "AddAudioTrack", s); }
                    finally { Com.Release(s); }
                }
                o.Progress?.Invoke(new BurnProgress { Phase = "Closing the disc" });
                Com.Call(tao, "ReleaseMedia");
                prepared = false;
                Finish(o, rec);
            }
            catch (COMException e) { throw Translate(e); }
            finally
            {
                activeWriter = null;
                if (prepared) try { Com.Call(tao, "ReleaseMedia"); } catch { }
                if (cp != null) try { cp.Unadvise(cookie); } catch { }
                Com.Release(tao); Com.Release(rec);
            }
        }

        public static void Erase(char letter, bool full, Action<BurnProgress> progress)
        {
            var rec = RequireRecorder(letter);
            var er = Com.Create("IMAPI2.MsftDiscFormat2Erase");
            IConnectionPoint cp = null; int cookie = 0;
            try
            {
                Com.Set(er, "Recorder", rec);
                Com.Set(er, "ClientName", ClientName);
                Com.Set(er, "FullErase", full);
                var sink = new EraseEventSink
                {
                    OnUpdate = (elapsed, total) => progress?.Invoke(new BurnProgress
                    {
                        Phase = "Erasing",
                        ElapsedSeconds = elapsed,
                        RemainingSeconds = Math.Max(0, total - elapsed),
                        Fraction = total > 0 ? Math.Min(1, elapsed / (double)total) : -1,
                    })
                };
                cookie = Advise(er, EraseEventsIid, sink, out cp);
                progress?.Invoke(new BurnProgress { Phase = "Erasing" });
                Com.Call(er, "EraseMedia");
                progress?.Invoke(new BurnProgress { Phase = "Erased", Fraction = 1 });
            }
            catch (COMException e) { throw Translate(e); }
            finally
            {
                if (cp != null) try { cp.Unadvise(cookie); } catch { }
                Com.Release(er); Com.Release(rec);
            }
        }

        static IStream OpenStream(string path)
        {
            Win32.SHCreateStreamOnFileEx(path, Win32.STGM_READ | Win32.STGM_SHARE_DENY_WRITE, 0, false, IntPtr.Zero, out var s);
            return s;
        }

        // ---- Error messages: IMAPI keeps its message tables in its own DLLs ----

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int FormatMessage(uint flags, IntPtr source, uint id, uint lang, StringBuilder buffer, int size, IntPtr args);

        /// <summary>IMAPI's own wording for an error, plus (when given) the step it failed at and the code, so a report pins it down.</summary>
        public static Exception Translate(COMException e, string step = null)
        {
            uint hr = unchecked((uint)e.ErrorCode);
            if (hr == 0xC0AA0002) return new OperationCanceledException("Cancelled.");
            string detail = step == null ? "" : $"\n\n(While {step}: 0x{hr:X8}.)";
            foreach (var dll in new[] { "imapi2.dll", "imapi2fs.dll" })
            {
                var mod = LoadLibraryEx(dll, IntPtr.Zero, 0x2);
                if (mod == IntPtr.Zero) continue;
                var sb = new StringBuilder(1024);
                if (FormatMessage(0x800 | 0x200, mod, hr, 0, sb, sb.Capacity, IntPtr.Zero) > 0)
                    return new DiscException(sb.ToString().Trim() + detail, e);
            }
            return new DiscException(e.Message + detail, e);
        }
    }
}
