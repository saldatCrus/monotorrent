//
// HttpTrackerTests.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2009 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using MonoTorrent.BEncoding;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.Trackers;

using NUnit.Framework;

namespace MonoTorrent.TrackerServer
{
    [TestFixture]
    public class HttpTrackerTests
    {
        MonoTorrent.Trackers.AnnounceRequest announceParams;
        MonoTorrent.Trackers.ScrapeRequest scrapeParams;
        TrackerServer server;
        HttpTrackerListener listener;
        string ListeningPrefix => "http://127.0.0.1:47124/";
        Uri AnnounceUrl => new Uri ($"{ListeningPrefix}announce");
        HttpClient client;
        HttpTrackerConnection trackerConnection;
        Tracker tracker;

        InfoHash infoHash;
        BEncodedString peerId;
        BEncodedString trackerId;

        readonly List<BEncodedString> keys = new List<BEncodedString> ();
        readonly List<InfoHash> announcedInfoHashes = new List<InfoHash> ();
        readonly List<InfoHash> scrapedInfoHashes = new List<InfoHash> ();

        [OneTimeSetUp]
        public void FixtureSetup ()
        {
            peerId = new BEncodedString (Enumerable.Repeat ((byte) 254, 20).ToArray ());
            trackerId = Enumerable.Repeat ((byte) 255, 20).ToArray ();
            listener = new HttpTrackerListener (ListeningPrefix);
            listener.AnnounceReceived += delegate (object o, AnnounceRequest e) {
                keys.Add (e.Key);
                announcedInfoHashes.Add (e.InfoHash);
            };
            listener.ScrapeReceived += (o, e) => {
                scrapedInfoHashes.AddRange (e.InfoHashes);
            };

            listener.Start ();
        }

        [SetUp]
        public void Setup ()
        {
            keys.Clear ();
            announcedInfoHashes.Clear ();
            scrapedInfoHashes.Clear ();

            listener.IncompleteAnnounce = listener.IncompleteScrape = false;

            server = new TrackerServer (trackerId) {
                AllowUnregisteredTorrents = true
            };
            server.RegisterListener (listener);
            client = new HttpClient ();
            trackerConnection = new HttpTrackerConnection (AnnounceUrl, client);
            tracker = new Tracker (trackerConnection);

            var infoHashBytes = new[] { ' ', '%', '&', '?', '&', '&', '?', '5', '1', '=' }
                        .Select (t => (byte) t);

            infoHash = new InfoHash (infoHashBytes.Concat (infoHashBytes).ToArray ());
            announceParams = new MonoTorrent.Trackers.AnnounceRequest (InfoHashes.FromV1 (infoHash))
                .WithPort (5555)
                .WithPeerId (peerId.Span.ToArray ());

            scrapeParams = new MonoTorrent.Trackers.ScrapeRequest (InfoHashes.FromV1 (infoHash));
        }

        [TearDown]
        public void Teardown ()
        {
            server.UnregisterListener (listener);
        }

        [OneTimeTearDown]
        public void FixtureTeardown ()
        {
            listener.Stop ();
            server.Dispose ();
        }

        [Test]
        public void CanAnnounceOrScrapeTest ()
        {
            HttpTrackerConnection t = new HttpTrackerConnection (new Uri ("http://mytracker.com/myurl"), new HttpClient ());
            Assert.IsFalse (t.CanScrape, "#1");

            t = new HttpTrackerConnection (new Uri ("http://mytracker.com/announce/yeah"), new HttpClient ());
            Assert.IsFalse (t.CanScrape, "#2");

            t = new HttpTrackerConnection (new Uri ("http://mytracker.com/announce"), new HttpClient ());
            Assert.IsTrue (t.CanScrape, "#3");
            Assert.AreEqual (t.ScrapeUri, new Uri ("http://mytracker.com/scrape"));

            t = new HttpTrackerConnection (new Uri ("http://mytracker.com/announce/yeah/announce"), new HttpClient ());
            Assert.IsTrue (t.CanScrape, "#4");
            Assert.AreEqual ("http://mytracker.com/announce/yeah/scrape", t.ScrapeUri.ToString (), "#4c");

            t = new HttpTrackerConnection (new Uri ("http://mytracker.com/announce/"), new HttpClient ());
            Assert.IsTrue (t.CanScrape, "#5");
            Assert.AreEqual (t.ScrapeUri, new Uri ("http://mytracker.com/scrape/"));
        }

        [Test]
        public async Task AnnounceHybrid ()
        {
            var hybrid = new InfoHashes (new InfoHash (Enumerable.Repeat<byte>(1, 20).ToArray ()), new InfoHash (Enumerable.Repeat<byte> (2, 32).ToArray ()));
            await tracker.AnnounceAsync (announceParams.WithInfoHashes (hybrid), CancellationToken.None);
            Assert.IsTrue (StringComparer.OrdinalIgnoreCase.Equals (keys[0], trackerConnection.Key), "#2");
            Assert.AreEqual (2, announcedInfoHashes.Count);
            Assert.IsTrue (announcedInfoHashes.Contains (hybrid.V1));
            Assert.IsTrue (announcedInfoHashes.Contains (hybrid.V2.Truncate ()));
        }

        [Test]
        public async Task AnnounceV1 ()
        {
            var v1 = new InfoHashes (new InfoHash (new byte[20]), null);
            await tracker.AnnounceAsync (announceParams.WithInfoHashes (v1), CancellationToken.None);
            Assert.IsTrue (StringComparer.OrdinalIgnoreCase.Equals (keys[0], trackerConnection.Key), "#2");
            Assert.AreEqual (1, announcedInfoHashes.Count);
            Assert.AreEqual (v1.V1, announcedInfoHashes[0]);
        }

        [Test]
        public async Task AnnounceV2 ()
        {
            var v2 = new InfoHashes (null, new InfoHash (new byte[32]));
            await tracker.AnnounceAsync (announceParams.WithInfoHashes (v2), CancellationToken.None);
            Assert.IsTrue (StringComparer.OrdinalIgnoreCase.Equals (keys[0], trackerConnection.Key), "#2");
            Assert.AreEqual (1, announcedInfoHashes.Count);
            Assert.AreEqual (v2.V2.Truncate (), announcedInfoHashes[0]);
        }

        [Test]
        public async Task Announce_ValidateParams ()
        {
            var argsTask = new TaskCompletionSource<AnnounceRequest> ();
            listener.AnnounceReceived += (o, e) => argsTask.TrySetResult (e);

            await tracker.AnnounceAsync (announceParams, CancellationToken.None);
            Assert.IsTrue (argsTask.Task.Wait (5000), "#1");

            var args = argsTask.Task.Result;
            Assert.AreEqual (peerId, BEncodedString.FromMemory (announceParams.PeerId), "#1");
            Assert.AreEqual (BEncodedString.FromMemory (announceParams.PeerId), args.PeerId, "#2");

            Assert.AreEqual (infoHash, args.InfoHash, "#3");
            Assert.IsTrue (announceParams.InfoHashes.V1OrV2 == args.InfoHash, "#3");
        }

        [Test]
        public async Task Announce_Incomplete ()
        {
            listener.IncompleteAnnounce = true;
            var response = await tracker.AnnounceAsync (announceParams, CancellationToken.None).WithTimeout ();
            Assert.AreEqual (TrackerState.InvalidResponse, tracker.Status);
            Assert.AreEqual (TrackerState.InvalidResponse, response.State);

            listener.IncompleteAnnounce = false;
            response = await tracker.AnnounceAsync (announceParams, CancellationToken.None);
            Assert.AreEqual (TrackerState.Ok, tracker.Status);
            Assert.AreEqual (TrackerState.Ok, response.State);
        }

        [Test]
        public async Task Announce_Timeout ()
        {
            TaskCompletionSource<bool> s = new TaskCompletionSource<bool> ();
            listener.AnnounceReceived += (o, e) => s.Task.Wait ();
            client.Timeout = TimeSpan.FromMilliseconds (1);
            try {
                var response = await tracker.AnnounceAsync (announceParams, CancellationToken.None).WithTimeout ();
                Assert.AreEqual (TrackerState.Offline, response.State);
            } finally {
                s.SetResult (true);
            }
            Assert.AreEqual (TrackerState.Offline, tracker.Status);
        }

        [Test]
        public async Task KeyTest ()
        {
            // Set a key which uses characters which need escaping.
            trackerConnection = new HttpTrackerConnection (AnnounceUrl, new HttpClient ());
            tracker = new Tracker (trackerConnection);
            trackerConnection.Key = peerId;

            await tracker.AnnounceAsync (announceParams, CancellationToken.None);
            Assert.AreEqual (peerId, keys[0], "#1");
        }

        [Test]
        public async Task NullKeyTest ()
        {
            // Set a key which uses characters which need escaping.
            trackerConnection = new HttpTrackerConnection (AnnounceUrl, new HttpClient ());
            tracker = new Tracker (trackerConnection);
            trackerConnection.Key = null;

            await tracker.AnnounceAsync (announceParams, CancellationToken.None);
            Assert.AreEqual (null, keys[0], "#1");
        }


        [Test]
        public async Task Scrape ()
        {
            // make sure it's a unique infohash as the listener isn't re-created for every test.
            infoHash = new InfoHash (Enumerable.Repeat ((byte) 1, 20).ToArray ());
            var infoHashes = InfoHashes.FromV1 (infoHash);
            var trackable = new InfoHashTrackable ("Test", infoHash);
            server.Add (trackable);
            scrapeParams = new MonoTorrent.Trackers.ScrapeRequest (infoHashes);

            await tracker.ScrapeAsync (scrapeParams, CancellationToken.None);
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Complete, "#1");
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Incomplete, "#2");
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Downloaded, "#3");

            await tracker.AnnounceAsync (new MonoTorrent.Trackers.AnnounceRequest (0, 0, 100, TorrentEvent.Started, infoHashes, false, new BEncodedString ("peer1").Span.ToArray (), null, 1, false), CancellationToken.None);
            await tracker.ScrapeAsync (scrapeParams, CancellationToken.None);
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Complete, "#4");
            Assert.AreEqual (1, tracker.ScrapeInfo[infoHash].Incomplete, "#5");
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Downloaded, "#6");

            await tracker.AnnounceAsync (new MonoTorrent.Trackers.AnnounceRequest (0, 0, 0, TorrentEvent.Started, infoHashes, false, new BEncodedString ("peer2").Span.ToArray (), null, 2, false), CancellationToken.None);
            await tracker.ScrapeAsync (scrapeParams, CancellationToken.None);
            Assert.AreEqual (1, tracker.ScrapeInfo[infoHash].Complete, "#7");
            Assert.AreEqual (1, tracker.ScrapeInfo[infoHash].Incomplete, "#8");
            Assert.AreEqual (0, tracker.ScrapeInfo[infoHash].Downloaded, "#9");

            await tracker.AnnounceAsync (new MonoTorrent.Trackers.AnnounceRequest (0, 0, 0, TorrentEvent.Completed, infoHashes, false, new BEncodedString ("peer3").Span.ToArray (), null, 3, false), CancellationToken.None);
            await tracker.ScrapeAsync (scrapeParams, CancellationToken.None);
            Assert.AreEqual (2, tracker.ScrapeInfo[infoHash].Complete, "#10");
            Assert.AreEqual (1, tracker.ScrapeInfo[infoHash].Incomplete, "#11");
            Assert.AreEqual (1, tracker.ScrapeInfo[infoHash].Downloaded, "#12");
        }


        [Test]
        public async Task ScrapeHybrid ()
        {
            var hybrid = new InfoHashes (new InfoHash (Enumerable.Repeat<byte> (1, 20).ToArray ()), new InfoHash (Enumerable.Repeat<byte> (2, 32).ToArray ()));
            await tracker.ScrapeAsync (new Trackers.ScrapeRequest (hybrid), CancellationToken.None);
            Assert.AreEqual (2, scrapedInfoHashes.Count);
            Assert.IsTrue (scrapedInfoHashes.Contains (hybrid.V1));
            Assert.IsTrue (scrapedInfoHashes.Contains (hybrid.V2.Truncate ()));
        }

        [Test]
        public async Task ScrapeV1 ()
        {
            var v1 = new InfoHashes (new InfoHash (new byte[20]), null);
            await tracker.ScrapeAsync (new Trackers.ScrapeRequest (v1), CancellationToken.None);
            Assert.AreEqual (1, scrapedInfoHashes.Count);
            Assert.AreEqual (v1.V1, scrapedInfoHashes[0]);
        }

        [Test]
        public async Task ScrapeV2 ()
        {
            var v2 = new InfoHashes (null, new InfoHash (new byte[32]));
            await tracker.ScrapeAsync (new Trackers.ScrapeRequest (v2), CancellationToken.None);
            Assert.AreEqual (1, scrapedInfoHashes.Count);
            Assert.AreEqual (v2.V2.Truncate (), scrapedInfoHashes[0]);
        }


        [Test]
        public async Task Scrape_Incomplete ()
        {
            listener.IncompleteScrape = true;
            client.Timeout = TimeSpan.FromHours (1);
            var response = await tracker.ScrapeAsync (scrapeParams, CancellationToken.None).WithTimeout ();
            Assert.AreEqual (TrackerState.InvalidResponse, tracker.Status);
            Assert.AreEqual (TrackerState.InvalidResponse, response.State);

            listener.IncompleteScrape = false;
            response = await tracker.ScrapeAsync (scrapeParams, CancellationToken.None);
            Assert.AreEqual (TrackerState.Ok, tracker.Status);
            Assert.AreEqual (TrackerState.Ok, response.State);
            Assert.IsNotNull (tracker.WarningMessage);
            Assert.IsNotNull (response.WarningMessage);
        }

        [Test]
        public async Task Scrape_Timeout ()
        {
            var tcs = new TaskCompletionSource<bool> ();
            listener.ScrapeReceived += (o, e) => tcs.Task.Wait ();
            client.Timeout = TimeSpan.FromMilliseconds (1);
            try {
                var response = await tracker.ScrapeAsync (scrapeParams, CancellationToken.None).WithTimeout ();
                Assert.AreEqual (TrackerState.Offline, response.State);
            } finally {
                tcs.SetResult (true);
            }
            Assert.AreEqual (TrackerState.Offline, tracker.Status);
        }

        [Test]
        public async Task TrackerId ()
        {
            // Null until the server side tracker sends us the value
            Assert.IsNull (trackerConnection.TrackerId, "#1");
            await tracker.AnnounceAsync (announceParams, CancellationToken.None);

            // Now we have the value, the next announce should contain it
            Assert.AreEqual (trackerId, trackerConnection.TrackerId, "#2");

            var argsTask = new TaskCompletionSource<AnnounceRequest> ();
            listener.AnnounceReceived += (o, e) => argsTask.TrySetResult (e);

            await tracker.AnnounceAsync (announceParams, CancellationToken.None);
            var result = await argsTask.Task.WithTimeout ("#3");
            Assert.AreEqual (trackerId, result.TrackerId, "#4");
        }
    }
}
