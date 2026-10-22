using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Discphew.Audio;
using Discphew.Disc;

namespace Discphew.UI
{
    sealed class PlayerView : ToolView
    {
        readonly Panel left = new Panel { Dock = DockStyle.Left, BackColor = Theme.Bg };
        readonly CoverBox cover = new CoverBox();
        readonly Caption albumLabel = new Caption("", Theme.Title, Theme.Text);
        readonly Caption artistLabel = new Caption("", Theme.Heading, Theme.Muted);
        readonly Caption infoLabel = new Caption("", Theme.Body, Theme.Faint);
        readonly FlatButton discButton = new FlatButton("Play disc", Icons.Disc);
        readonly FlatButton folderButton = new FlatButton("Open album folder…", Icons.Folder);

        readonly Card listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(6)) };
        readonly DarkGrid grid = new DarkGrid { Dock = DockStyle.Fill };

        readonly Card transport = new Card { Dock = DockStyle.Bottom };
        readonly Caption nowTitle = new Caption("", Theme.Strong, Theme.Text);
        readonly Caption nowArtist = new Caption("", Theme.Small, Theme.Muted);
        readonly FlatButton prevButton = new FlatButton("", Icons.Prev) { Subtle = true, IconSize = 18 };
        readonly FlatButton playButton = new FlatButton("", Icons.Play) { Primary = true, IconSize = 20 };
        readonly FlatButton nextButton = new FlatButton("", Icons.Next) { Subtle = true, IconSize = 18 };
        readonly FlatButton stopButton = new FlatButton("", Icons.Stop) { Subtle = true, IconSize = 16 };
        readonly FlatButton shuffleButton = new FlatButton("", Icons.Shuffle) { Subtle = true, IconSize = 18 };
        readonly Slider seek = new Slider();
        readonly Caption timeLeft = new Caption("0:00", Theme.Small, Theme.Muted);
        readonly Caption timeRight = new Caption("0:00", Theme.Small, Theme.Muted);
        readonly Slider volume = new Slider();
        readonly Panel volumeIcon = new Panel { BackColor = Color.Transparent };
        readonly Timer tick = new Timer { Interval = 100 };

        IAlbumSource source;
        WavePlayer player;
        readonly object sourceLock = new object();
        int currentTrack = -1;
        double gain = 1;
        bool playing;

        // Shuffle: the order tracks play in (built when playback starts), and how far Fill has got through it.
        bool shuffle;
        List<int> order;
        int orderPos;
        readonly Random rng = new Random();

        // Shuffle jumps around the source inside Fill, so what the player has played is no longer one continuous
        // stretch of the album. Each entry is where a stretch starts: frames handed to the player since the last
        // flush, and the source frame it came from. Guarded by sourceLock.
        readonly List<(long output, long source)> jumps = new List<(long, long)> { (0, 0) };
        long produced;
        byte[] scratch = new byte[0];

        // Covers found on MusicBrainz for albums that came without one, by artist + album (null: none found).
        readonly Dictionary<string, byte[]> foundCovers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public PlayerView(AppState state) : base(state)
        {
            left.Width = Theme.S(360);
            left.Padding = new Padding(0, 0, Theme.S(24), 0);
            discButton.Width = discButton.PreferredWidth;
            folderButton.Width = folderButton.PreferredWidth;
            discButton.Click += (s, e) => LoadDisc(true);
            folderButton.Click += (s, e) => OpenFolder();
            left.Controls.AddRange(new Control[] { cover, albumLabel, artistLabel, infoLabel, discButton, folderButton });
            left.Resize += (s, e) => LayoutLeft();

            grid.AddText("", "playing", 40);
            grid.AddText("#", "num", 44, right: true);
            grid.AddText("Title", "title", fill: true);
            grid.AddText("Artist", "artist", 220);
            grid.AddText("Length", "len", 76, right: true);
            grid.ReadOnly = true;
            grid.CellPainting += OnCellPainting;
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) PlayTrack(e.RowIndex); };
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && grid.CurrentRow != null) { PlayTrack(grid.CurrentRow.Index); e.Handled = true; } };
            listCard.Controls.Add(grid);

            transport.Height = Theme.S(96);
            playButton.Circle = true;
            playButton.Size = new Size(Theme.S(48), Theme.S(48));
            foreach (var b in new[] { prevButton, nextButton, stopButton }) b.Size = new Size(Theme.S(40), Theme.S(40));
            prevButton.Click += (s, e) => Previous();
            nextButton.Click += (s, e) => Next();
            playButton.Click += (s, e) => TogglePlay();
            stopButton.Click += (s, e) => Stop();
            shuffleButton.Size = stopButton.Size;
            shuffleButton.Click += (s, e) => SetShuffle(!shuffle);
            SetShuffle(Settings.GetBool("player.shuffle", false), remember: false);
            seek.Scrubbed += v => timeLeft.Text = Fmt.Time(TimeSpan.FromSeconds(v * (source?.TotalFrames ?? 0) / (double)(source?.SampleRate ?? 44100)));
            seek.Committed += v => { if (source != null) SeekTo((long)(v * source.TotalFrames)); };
            volume.Value = Settings.GetInt("player.volume", 85) / 100.0;
            gain = volume.Value * volume.Value;
            volume.Scrubbed += v => { gain = v * v; };
            volume.Committed += v => { gain = v * v; Settings.Set("player.volume", (int)(v * 100)); };
            volumeIcon.Paint += (s, e) => { Theme.Hq(e.Graphics); Icons.Volume(e.Graphics, new RectangleF(0, 0, volumeIcon.Width, volumeIcon.Height), Theme.Muted); };
            transport.Controls.AddRange(new Control[] { nowTitle, nowArtist, shuffleButton, prevButton, playButton, nextButton, stopButton, seek, timeLeft, timeRight, volumeIcon, volume });
            transport.Resize += (s, e) => LayoutTransport();
            timeRight.TextAlign = ContentAlignment.MiddleRight;

            var gap = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(16), BackColor = Theme.Bg };
            Controls.Add(listCard);
            Controls.Add(left);
            Controls.Add(gap);
            Controls.Add(transport);

            tick.Tick += (s, e) => UpdatePosition();
            state.Changed += () => { if (State.Busy && source != null && source.IsDisc) Stop(true); };
            UpdateTransport();
        }

        public override string Title => "Album Player";

        public override string Subtitle =>
            source != null ? (source.IsDisc ? "Straight from the disc — gapless, pregaps and all" : "From " + ((FolderAlbumSource)source).Folder + " — gapless")
            : State.Disc?.HasAudio == true ? "Press play to listen to the disc as an album" : "Insert an audio CD or open a folder of FLAC/WAV files";

        // ------------------------------------------------------------------ layout

        void LayoutLeft()
        {
            int w = left.Width - left.Padding.Right;
            int size = Math.Min(w, Math.Max(Theme.S(120), left.Height - Theme.S(220)));
            cover.SetBounds(0, 0, size, size);
            albumLabel.SetBounds(0, cover.Bottom + Theme.S(18), w, albumLabel.Height);
            artistLabel.SetBounds(0, albumLabel.Bottom, w, artistLabel.Height);
            infoLabel.SetBounds(0, artistLabel.Bottom + Theme.S(2), w, infoLabel.Height);
            discButton.Location = new Point(0, infoLabel.Bottom + Theme.S(16));
            folderButton.Location = new Point(discButton.Right + Theme.S(8), discButton.Top);
        }

        void LayoutTransport()
        {
            int pad = transport.Padding.Left, h = transport.Height;
            int cx = transport.Width / 2;
            int controlsY = Theme.S(10);
            playButton.Location = new Point(cx - playButton.Width / 2, controlsY - Theme.S(2));
            prevButton.Location = new Point(playButton.Left - Theme.S(10) - prevButton.Width, controlsY + (playButton.Height - prevButton.Height) / 2 - Theme.S(2));
            nextButton.Location = new Point(playButton.Right + Theme.S(10), prevButton.Top);
            stopButton.Location = new Point(nextButton.Right + Theme.S(6), prevButton.Top);
            shuffleButton.Location = new Point(prevButton.Left - Theme.S(6) - shuffleButton.Width, prevButton.Top);
            int seekW = Math.Min(Theme.S(560), transport.Width - Theme.S(560));
            seekW = Math.Max(Theme.S(200), seekW);
            int sy = playButton.Bottom + Theme.S(6);
            timeLeft.SetBounds(cx - seekW / 2 - Theme.S(54), sy - Theme.S(2), Theme.S(48), seek.Height + Theme.S(4));
            timeLeft.TextAlign = ContentAlignment.MiddleRight;
            seek.SetBounds(cx - seekW / 2, sy, seekW, seek.Height);
            timeRight.SetBounds(seek.Right + Theme.S(6), sy - Theme.S(2), Theme.S(48), seek.Height + Theme.S(4));
            int textW = Math.Max(10, shuffleButton.Left - pad - Theme.S(20));
            nowTitle.SetBounds(pad, h / 2 - nowTitle.Height, textW, nowTitle.Height);
            nowArtist.SetBounds(pad, h / 2, textW, nowArtist.Height);
            volume.SetBounds(transport.Width - pad - Theme.S(120), (h - volume.Height) / 2, Theme.S(120), volume.Height);
            volumeIcon.SetBounds(volume.Left - Theme.S(28), (h - Theme.S(20)) / 2, Theme.S(20), Theme.S(20));
        }

        // ------------------------------------------------------------------ sources

        public override void OnDiscChanged()
        {
            if (source != null && source.IsDisc && (State.Disc == null || !State.Disc.HasAudio || State.Disc.Drive != State.Drive))
                Stop(true);
            if (source == null) ShowAlbum(null);
            else if (source.IsDisc && player == null && State.Disc != null && State.Disc.HasAudio) LoadDisc(false);   // pick up edited metadata
            discButton.Enabled = State.Disc?.HasAudio == true && !State.Busy;
        }

        public override void OnActivated()
        {
            if (source == null && State.Disc?.HasAudio == true && !State.Busy) LoadDisc(false);
        }

        void ShowAlbum(IAlbumSource s)
        {
            var d = State.Disc;
            grid.Rows.Clear();
            if (s != null)
            {
                albumLabel.Text = s.Album;
                artistLabel.Text = s.Artist;
                infoLabel.Text = string.Join("  ·  ", new[] { s.Year, $"{s.Tracks.Count} tracks", Fmt.Time(TimeSpan.FromSeconds(s.TotalFrames / (double)s.SampleRate)), s.SampleRate != 44100 ? $"{s.SampleRate / 1000.0:0.#} kHz" : "" }.Where(x => !string.IsNullOrEmpty(x)));
                cover.LoadBytes(s.Cover);
                if (s.Cover == null)
                    FindCover(s.Artist, s.Album, bytes =>
                    {
                        if (s.IsDisc && d != null) MetadataCache.SaveCover(d, bytes);
                        if (ReferenceEquals(source, s)) cover.LoadBytes(bytes);
                    });
                foreach (var t in s.Tracks) grid.Rows.Add("", t.Number.ToString(), t.Title, t.Artist, Fmt.Time(t.Duration(s.SampleRate)));
            }
            else if (d != null && d.HasAudio)
            {
                albumLabel.Text = d.AlbumTitle.Length > 0 ? d.AlbumTitle : "Audio CD";
                artistLabel.Text = d.AlbumArtist;
                infoLabel.Text = $"{(d.Year.Length > 0 ? d.Year + "  ·  " : "")}{d.AudioTracks.Count()} tracks  ·  {Fmt.Time(d.AudioDuration)}";
                cover.LoadFile(d.CoverPath);
                if (cover.Image == null)
                    FindCover(d.AlbumArtist, d.AlbumTitle, bytes =>
                    {
                        MetadataCache.SaveCover(d, bytes);
                        if (ReferenceEquals(State.Disc, d) && (source == null || source.IsDisc)) cover.LoadBytes(bytes);
                    });
                foreach (var t in d.AudioTracks) grid.Rows.Add("", t.Number.ToString(), t.Title.Length > 0 ? t.Title : $"Track {t.Number}", t.Artist, Fmt.Time(t.Duration));
            }
            else
            {
                albumLabel.Text = "Nothing playing";
                artistLabel.Text = "";
                infoLabel.Text = "Insert an audio CD or open an album folder.";
                cover.Image = null;
            }
            (Owner as MainForm)?.UpdateHeader();
            UpdateTransport();
        }

        /// <summary>
        /// For an album with no art of its own (a burned CD's CD-Text, a folder without a cover): looks the cover
        /// up on MusicBrainz by name, once per album, and hands it to <paramref name="found"/> on the UI thread.
        /// </summary>
        void FindCover(string artist, string album, Action<byte[]> found)
        {
            if (string.IsNullOrWhiteSpace(album)) return;
            string key = artist + "\n" + album;
            if (foundCovers.TryGetValue(key, out var known)) { if (known != null) found(known); return; }
            foundCovers[key] = null;
            Task.Run(() => MusicBrainz.SearchCover(artist, album)).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result == null) return;
                foundCovers[key] = t.Result;
                found(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void LoadDisc(bool autoplay)
        {
            var d = State.Disc;
            if (d == null || !d.HasAudio || State.Busy) return;
            Stop(true);
            try { SetSource(new CdAlbumSource(d)); }
            catch (Exception e) { Dialogs.Error(Owner, "Can't play this disc", e.Message); return; }
            if (autoplay) Play();
        }

        void OpenFolder()
        {
            var folder = Ui.PickFolder(Owner, "Choose an album folder (FLAC, WAV, or anything ffmpeg can read)", Settings.Get("player.folder", Ui.DefaultMusicFolder));
            if (folder == null) return;
            Settings.Set("player.folder", folder);
            Stop(true);
            Cursor = Cursors.WaitCursor;
            Task.Run(() => (IAlbumSource)new FolderAlbumSource(folder)).ContinueWith(t =>
            {
                Cursor = Cursors.Default;
                if (t.IsFaulted) { Dialogs.Error(Owner, "Can't open that folder", t.Exception.GetBaseException().Message); return; }
                SetSource(t.Result);
                Play();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetSource(IAlbumSource s)
        {
            lock (sourceLock)
            {
                source = s;
                order = null;
                ResetMap(0);
            }
            currentTrack = -1;
            ShowAlbum(s);
        }

        // ------------------------------------------------------------------ shuffle

        void SetShuffle(bool on, bool remember = true)
        {
            shuffle = on;
            if (remember) Settings.Set("player.shuffle", on ? "1" : "0");
            lock (sourceLock) order = null;         // Fill deals a fresh order from wherever playback has got to
            shuffleButton.ForeColor = on ? Theme.Accent : Theme.Text;
            shuffleButton.Invalidate();
        }

        /// <summary>Makes <paramref name="track"/> the one playing in the shuffled order: tracks not yet played stay ahead of it. Under sourceLock.</summary>
        void Promote(int track)
        {
            int i = order.IndexOf(track);
            if (i < 0) return;
            order.RemoveAt(i);
            if (i <= orderPos) orderPos--;
            order.Insert(++orderPos, track);
        }

        /// <summary>Starts the position map over at <paramref name="sourceFrame"/> (after a flush, or with a new source). Under sourceLock.</summary>
        void ResetMap(long sourceFrame)
        {
            jumps.Clear();
            jumps.Add((0, sourceFrame));
            produced = 0;
            if (order != null && source != null) Promote(TrackAt(sourceFrame));
        }

        /// <summary>The source frame behind the <paramref name="output"/>th frame handed to the player since the last flush. Under sourceLock.</summary>
        long SourceFrame(long output)
        {
            int i = jumps.Count - 1;
            while (i > 0 && jumps[i].output > output) i--;
            return jumps[i].source + (output - jumps[i].output);
        }

        long TrackEnd(int i) => i + 1 < source.Tracks.Count ? source.Tracks[i + 1].StartFrame : source.TotalFrames;

        // ------------------------------------------------------------------ transport

        int Fill(byte[] buffer, int count)
        {
            int n = 0;
            lock (sourceLock)
            {
                if (source == null) return 0;
                try
                {
                    if (shuffle && order == null && source.Tracks.Count > 0)
                    {
                        int first = TrackAt(SourceFrame(produced));
                        order = new List<int> { first };
                        order.AddRange(Enumerable.Range(0, source.Tracks.Count).Where(i => i != first).OrderBy(_ => rng.Next()));
                        orderPos = 0;
                    }
                    while (n < count)
                    {
                        int want = count - n;
                        if (order != null)
                        {
                            // Play to the last sample of this track, then carry straight on from the start of the next one in the order.
                            long at = SourceFrame(produced), end = TrackEnd(order[orderPos]);
                            if (at >= end)
                            {
                                if (orderPos + 1 >= order.Count) break;
                                long next = source.Tracks[order[++orderPos]].StartFrame;
                                source.Seek(next);
                                jumps.Add((produced, next));
                                continue;
                            }
                            want = (int)Math.Min(want, (end - at) * 4);
                        }
                        if (scratch.Length < want) scratch = new byte[count];
                        int got = source.Read(scratch, want);
                        if (got <= 0) break;
                        Buffer.BlockCopy(scratch, 0, buffer, n, got);
                        n += got;
                        produced += got / 4;
                    }
                }
                catch { if (n == 0) return 0; }
            }
            double g = gain;
            if (g < 0.999)
            {
                int gi = (int)(g * 65536);
                for (int i = 0; i + 1 < n; i += 2)
                {
                    int v = (short)(buffer[i] | (buffer[i + 1] << 8));
                    v = (v * gi) >> 16;
                    buffer[i] = (byte)v; buffer[i + 1] = (byte)(v >> 8);
                }
            }
            return n;
        }

        void EnsurePlayer()
        {
            if (player != null || source == null) return;
            player = new WavePlayer(source.SampleRate, 2, Fill);
            player.Finished += () => BeginInvoke(new Action(() => { Stop(false); }));
        }

        void Play()
        {
            if (source == null) { if (State.Disc?.HasAudio == true) LoadDisc(true); return; }
            EnsurePlayer();
            if (player == null) return;
            player.Resume();
            playing = true;
            tick.Start();
            UpdateTransport();
        }

        void Pause()
        {
            player?.Pause();
            playing = false;
            UpdateTransport();
        }

        void TogglePlay()
        {
            if (playing) Pause(); else Play();
        }

        /// <param name="release">Also close the source (frees the drive).</param>
        void Stop(bool release = false)
        {
            playing = false;
            tick.Stop();
            player?.Dispose();
            player = null;
            lock (sourceLock)
            {
                source?.Seek(0);
                order = null;
                ResetMap(0);
                if (release) { source?.Dispose(); source = null; }
            }
            seek.Value = 0;
            timeLeft.Text = "0:00";
            currentTrack = -1;
            grid.Invalidate();
            UpdateTransport();
            if (release) ShowAlbum(null);
        }

        void SeekTo(long frame)
        {
            if (source == null) return;
            frame = Math.Max(0, Math.Min(source.TotalFrames - 1, frame));
            void Restart() { lock (sourceLock) { source.Seek(frame); ResetMap(frame); } }
            if (player == null)
            {
                lock (sourceLock) source.Seek(frame);
                EnsurePlayer();
                player?.Flush(frame, Restart);
                Play();
            }
            else player.Flush(frame, Restart);
            UpdatePosition();
        }

        void PlayTrack(int index)
        {
            if (source == null) { LoadDisc(false); if (source == null) return; }
            if (index < 0 || index >= source.Tracks.Count) return;
            SeekTo(source.Tracks[index].StartFrame);
            if (!playing) Play();
        }

        /// <summary>Where in the album the audio being heard comes from.</summary>
        long Position
        {
            get
            {
                if (player == null) return 0;
                long played = player.PositionFrames;
                lock (sourceLock) return SourceFrame(played - jumps[0].source);    // the player counts from the flush's base
            }
        }

        int TrackAt(long frame)
        {
            if (source == null) return -1;
            for (int i = source.Tracks.Count - 1; i >= 0; i--)
                if (frame >= source.Tracks[i].StartFrame) return i;
            return 0;
        }

        void Previous()
        {
            if (source == null) return;
            long pos = Position;
            int i = TrackAt(pos);
            // Like a CD player: restart the track unless we're within the first 3 seconds.
            bool back = pos - source.Tracks[i].StartFrame < source.SampleRate * 3;
            lock (sourceLock)
            {
                int p = order?.IndexOf(i) ?? -1;
                if (p >= 0)
                {
                    // Shuffled: step back through the order played so far.
                    if (back && p > 0) { orderPos = p - 1; i = order[p - 1]; }
                    else orderPos = p;
                    back = false;
                }
            }
            if (back && i > 0) i--;
            PlayTrack(Math.Max(0, i));
        }

        void Next()
        {
            if (source == null) return;
            int i = TrackAt(Position);
            lock (sourceLock)
            {
                int p = order?.IndexOf(i) ?? -1;
                if (p >= 0) { if (p + 1 < order.Count) { orderPos = p; i = order[p + 1]; } else return; }
                else i++;
            }
            if (i < source.Tracks.Count) PlayTrack(i);
        }

        void UpdatePosition()
        {
            if (source == null) return;
            long pos = Position;
            if (!seek.Dragging)
            {
                seek.Value = source.TotalFrames > 0 ? pos / (double)source.TotalFrames : 0;
                timeLeft.Text = Fmt.Time(TimeSpan.FromSeconds(pos / (double)source.SampleRate));
            }
            timeRight.Text = Fmt.Time(TimeSpan.FromSeconds(source.TotalFrames / (double)source.SampleRate));
            int t = TrackAt(pos);
            if (playing && t >= 0 && t < grid.RowCount) grid.InvalidateCell(0, t);
            if (t != currentTrack)
            {
                currentTrack = t;
                grid.Invalidate();
                if (t >= 0)
                {
                    nowTitle.Text = source.Tracks[t].Title;
                    nowArtist.Text = $"{source.Tracks[t].Artist}  ·  track {source.Tracks[t].Number} of {source.Tracks.Count}";
                }
            }
        }

        void UpdateTransport()
        {
            playButton.Icon = playing ? (Icons.Painter)Icons.Pause : Icons.Play;
            playButton.Invalidate();
            bool has = source != null || State.Disc?.HasAudio == true;
            playButton.Enabled = has && !State.Busy;
            prevButton.Enabled = nextButton.Enabled = stopButton.Enabled = seek.Enabled = source != null;
            if (source == null || currentTrack < 0)
            {
                nowTitle.Text = source != null ? (playing ? "" : "Ready") : "";
                nowArtist.Text = source != null ? source.Album : "";
            }
            if (source != null) timeRight.Text = Fmt.Time(TimeSpan.FromSeconds(source.TotalFrames / (double)source.SampleRate));
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            bool now = e.RowIndex == currentTrack && source != null;
            if (e.ColumnIndex == 0)
            {
                e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                if (now)
                {
                    Theme.Hq(e.Graphics);
                    float s = Theme.S(14f);
                    var r = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - s) / 2, e.CellBounds.Y + (e.CellBounds.Height - s) / 2, s, s);
                    if (playing) DrawBars(e.Graphics, r); else Icons.Pause(e.Graphics, r, Theme.Accent);
                }
                e.Handled = true;
                return;
            }
            if (now)
            {
                e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                var flags = e.ColumnIndex == 1 || e.ColumnIndex == 4 ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? "", e.ColumnIndex == 2 ? Theme.Strong : Theme.Body,
                    new Rectangle(e.CellBounds.X + Theme.S(6), e.CellBounds.Y, e.CellBounds.Width - Theme.S(12), e.CellBounds.Height), Theme.Accent,
                    flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                e.Handled = true;
            }
        }

        void DrawBars(Graphics g, RectangleF r)
        {
            double t = Environment.TickCount / 180.0;
            for (int i = 0; i < 3; i++)
            {
                float h = (float)(0.35 + 0.65 * Math.Abs(Math.Sin(t + i * 1.7))) * r.Height;
                Theme.FillRound(g, Theme.Accent, new RectangleF(r.X + i * r.Width * 0.36f, r.Bottom - h, r.Width * 0.24f, h), 1.5f);
            }
        }

        public override bool HandleKey(Keys key)
        {
            switch (key)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Right: if (source != null) SeekTo(Position + source.SampleRate * 5); return true;
                case Keys.Left: if (source != null) SeekTo(Position - source.SampleRate * 5); return true;
                case Keys.Control | Keys.Right: Next(); return true;
                case Keys.Control | Keys.Left: Previous(); return true;
                case Keys.S: SetShuffle(!shuffle); return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose();
                player?.Dispose();
                player = null;
                source?.Dispose();
                source = null;
            }
            base.Dispose(disposing);
        }
    }
}
