using System;
using System.Collections.Generic;
using System.Linq;
using MonoTorrent.Logging;

namespace MonoTorrent.Client.PiecePicking
{
    class LoggingPiecePicker : IPiecePicker
    {
        static readonly Logger Logger = Logger.Create ();

        IPiecePicker Next { get; }
        Dictionary<IPeer, List<(PieceRequest request, DateTime requestedAt)>> Requests { get; }

        public LoggingPiecePicker (IPiecePicker next)
        {
            Next = next ?? throw new ArgumentNullException (nameof (next));
            Requests = new Dictionary<IPeer, List<(PieceRequest, DateTime)>> ();
        }

        public int AbortRequests (IPeer peer)
        {
            int count = Next.AbortRequests (peer);
            if (Requests.TryGetValue (peer, out var requests)) {
                Requests.Remove (peer);
                if (requests.Count != count)
                    throw new InvalidOperationException ($"Expected {count} requests to abort but were {requests.Count} requests");
                foreach (var (request, requestedAt) in requests)
                    Log (peer, request, "Aborting");
            } else if (count > 0) {
                throw new InvalidOperationException ($"{count} request(s) were aborted but none were tracked");
            }
            return count;
        }

        public IList<PieceRequest> CancelRequests (IPeer peer, int startIndex, int endIndex)
        {
            var cancelled = Next.CancelRequests (peer, startIndex, endIndex);
            if (Requests.TryGetValue (peer, out var requests)) {
                var toCancel = requests.Select (t => t.request).Where (t => t.PieceIndex >= startIndex && t.PieceIndex <= endIndex).ToList ();
                if (toCancel.Count != requests.Count)
                    throw new InvalidOperationException ($"Expected {toCancel.Count} requests to cancel but were {requests.Count} requests");
                if (cancelled.Except (toCancel).ToArray ().Length != 0)
                    throw new InvalidOperationException ($"The set of pieces didn't match when cancelling");
                requests.RemoveAll (t => toCancel.Contains (t.request));
                foreach (var v in toCancel)
                    Log (peer, v, "Cancel");
            } else if (cancelled.Count > 0) {
                throw new InvalidOperationException ($"{cancelled.Count} request(s) were aborted but none were tracked");
            }
            return cancelled;
        }

        public PieceRequest? ContinueAnyExistingRequest (IPeer peer, int startIndex, int endIndex, int maxDuplicateRequests)
        {
            var request = Next.ContinueAnyExistingRequest (peer, startIndex, endIndex, maxDuplicateRequests);
            if (request.HasValue) {
                if (!Requests.TryGetValue (peer, out var list))
                    Requests[peer] = list = new List<(PieceRequest request, DateTime requestedAt)> ();
                list.Add ((request.Value, DateTime.Now));
                Log (peer, request.Value, "ContinueAnyExistingRequest");
            }
            return request;
        }

        public PieceRequest? ContinueExistingRequest (IPeer peer, int startIndex, int endIndex)
        {
            var request = Next.ContinueExistingRequest (peer, startIndex, endIndex);
            if (request.HasValue) {
                if (!Requests.TryGetValue (peer, out var list))
                    Requests[peer] = list = new List<(PieceRequest request, DateTime requestedAt)> ();
                list.Add ((request.Value, DateTime.Now));
                Log (peer, request.Value, "ContinueExistingRequest");
            }
            return request;
        }

        public int CurrentReceivedCount ()
        {
            return Next.CurrentReceivedCount ();
        }

        public int CurrentRequestCount ()
        {
            return Next.CurrentRequestCount ();
        }

        public IList<ActivePieceRequest> ExportActiveRequests ()
        {
            return Next.ExportActiveRequests ();
        }

        public void Initialise (BitField bitfield, ITorrentData torrentData, IEnumerable<ActivePieceRequest> requests)
        {
            Requests.Clear ();
            Next.Initialise (bitfield, torrentData, requests);
        }

        public bool IsInteresting (IPeer peer, BitField bitfield)
        {
            return Next.IsInteresting (peer, bitfield);
        }

        public IList<PieceRequest> PickPiece (IPeer peer, BitField available, IReadOnlyList<IPeer> otherPeers, int count, int startIndex, int endIndex)
        {
            var bundle = Next.PickPiece (peer, available, otherPeers, count, startIndex, endIndex);
            if (bundle != null) {
                if (!Requests.TryGetValue (peer, out var list))
                    Requests[peer] = list = new List<(PieceRequest request, DateTime requestedAt)> ();
                foreach (var v in bundle) {
                    Log (peer, v, "PickPiece");
                    list.Add ((v, DateTime.Now));
                }
            }
            return bundle;
        }

        public void RequestRejected (IPeer peer, PieceRequest request)
        {
            Next.RequestRejected (peer, request);
            if (!Requests.TryGetValue (peer, out var list))
                throw new InvalidOperationException ("Received a reject request but no pieces were requested.");

            if (!list.Exists (t => t.request == request))
                throw new InvalidOperationException ("Received a reject request for a piece which wasn't requested.");
            list.RemoveAll (t => t.request == request);
        }

        public bool ValidatePiece (IPeer peer, PieceRequest request, out bool pieceComplete, out IList<IPeer> peersInvolved)
        {
            if (!Requests.TryGetValue (peer, out var list))
                throw new InvalidOperationException ("Received a piece but no pieces were requested.");

            var result = Next.ValidatePiece (peer, request, out pieceComplete, out peersInvolved);

            if (list.Exists (t => t.request == request)) {
                Log (peer, request, "ValidatePiece");
                list.RemoveAll (t => t.request == request);
            } else {
                Log (peer, request, "ValidatePiece - MISSING");
            }
            return result;
        }

        static void Log(IPeer peer, PieceRequest request, string prefix)
        {
            Logger.InfoTimeFormatted ("{0} - {1} - {2}", prefix, request, peer);
        }
    }
}
