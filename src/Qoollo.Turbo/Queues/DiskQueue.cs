﻿using Qoollo.Turbo.Queues.DiskQueueComponents;
using Qoollo.Turbo.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Qoollo.Turbo.Collections;
using System.IO;
using System.Diagnostics;
using Qoollo.Turbo.Threading.ThreadManagement;

namespace Qoollo.Turbo.Queues
{
    /// <summary>
    /// Queue that stores items on disk
    /// </summary>
    /// <typeparam name="T">Type of items stored inside queue</typeparam>
    public class DiskQueue<T>: Common.CommonQueueImpl<T>
    {
        private const int CompactionPeriodMs = 2 * 60 * 1000;

        private readonly DiskQueueSegmentFactory<T> _segmentFactory;
        private readonly MonitorObject _segmentOperationsMonitor;
        private readonly int _maxSegmentCount;
        private readonly string _segmentsPath;

        private readonly CircularList<DiskQueueSegment<T>> _segments;
        private volatile DiskQueueSegment<T> _tailSegment;
        private volatile DiskQueueSegment<T> _headSegment;

        private long _lastSegmentNumber;

        private long _itemCount;
        private readonly long _boundedCapacity;

        private readonly DelegateThreadSetManager _backgroundCompactionThread;

        private volatile bool _isDisposed;


        public DiskQueue(string path, DiskQueueSegmentFactory<T> segmentFactory, int maxSegmentCount, bool backgroundCompaction)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (segmentFactory == null)
                throw new ArgumentNullException(nameof(segmentFactory));

            if (maxSegmentCount <= 0 || maxSegmentCount > int.MaxValue / 4)
                maxSegmentCount = int.MaxValue / 4;

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            _segmentFactory = segmentFactory;
            _segmentOperationsMonitor = new MonitorObject("DiskQueue.SegmentOperations");
            _maxSegmentCount = maxSegmentCount;
            _segmentsPath = path;

            _itemCount = 0;
            _boundedCapacity = -1;
            if (_segmentFactory.SegmentCapacity > 0)
                _boundedCapacity = (long)_segmentFactory.SegmentCapacity * maxSegmentCount;


            var discoveredSegments = segmentFactory.DiscoverSegments(path);
            if (discoveredSegments == null || discoveredSegments.Any(o => o == null))
                throw new InvalidOperationException("Existed segment discovery returned null");

            _segments = new CircularList<DiskQueueSegment<T>>(discoveredSegments.OrderBy(o => o.Number));
            if (_segments.Count > 0)
            {
                _headSegment = _segments[0];
                _tailSegment = _segments[_segments.Count - 1];
                _lastSegmentNumber = _tailSegment.Number;
            }
            else
            {
                // Allocate new segment
                lock (_segmentOperationsMonitor)
                {
                    _headSegment = _tailSegment = AllocateNewSegment();
                }
            }

            if (backgroundCompaction)
            {
                _backgroundCompactionThread = new DelegateThreadSetManager(1, this.GetType().GetCSName() + "_" + this.GetHashCode().ToString() + " Background compaction", BackgroundCompactionProc);
                _backgroundCompactionThread.IsBackground = true;
                _backgroundCompactionThread.Start();
            }

            _isDisposed = false;
        }


        /// <summary>
        /// The bounded size of the queue (-1 means not bounded)
        /// </summary>
        public override long BoundedCapacity { get { return _boundedCapacity; } }
        /// <summary>
        /// Number of items inside the queue
        /// </summary>
        public override long Count { get { return Volatile.Read(ref _itemCount); } }
        /// <summary>
        /// Indicates whether the queue is empty
        /// </summary>
        public override bool IsEmpty { get { return Volatile.Read(ref _itemCount) == 0; } }
        /// <summary>
        /// Is background compaction enabled
        /// </summary>
        public bool IsBackgroundCompactionEnabled { get { return _backgroundCompactionThread != null; } }


        /// <summary>
        /// Checks if queue is disposed
        /// </summary>
        private void CheckDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(this.GetType().Name);
        }

        /// <summary>
        /// Removes completed segments
        /// </summary>
        private void Compact()
        {
            if (_isDisposed)
                return;

            List<DiskQueueSegment<T>> _segmentsToCleanUp = null;
            lock (_segmentOperationsMonitor)
            {
                if (_isDisposed)
                    return;

                Debug.Assert(_tailSegment != null);
                Debug.Assert(_segments.Count > 0);
                Debug.Assert(_tailSegment == _segments[_segments.Count - 1]);
                Debug.Assert(_headSegment != null);
                Debug.Assert(_segments.Contains(_headSegment));
                Debug.Assert(_segments.TakeWhile(o => o != _headSegment).All(o => o.IsCompleted));
                Debug.Assert(_segments.IndexOf(_headSegment) <= _segments.IndexOf(_tailSegment));

                MoveHeadToNonCompletedSegment(); // Correct head segment
                while (_segments.Count > 1 && _headSegment != _segments[0] && _segments[0].IsCompleted)
                {
                    _segmentsToCleanUp = _segmentsToCleanUp ?? new List<DiskQueueSegment<T>>();
                    _segmentsToCleanUp.Add(_segments.RemoveFirst());
                }
            }

            if (_segmentsToCleanUp != null)
                foreach (var segment in _segmentsToCleanUp)
                    segment.Dispose(DiskQueueSegmentDisposeBehaviour.Delete);
        }

        /// <summary>
        /// Creates a new segment
        /// </summary>
        /// <returns>Created segment</returns>
        private DiskQueueSegment<T> AllocateNewSegment()
        {
            CheckDisposed();

            Debug.Assert(_segmentOperationsMonitor.IsEntered());

            var result = _segmentFactory.CreateSegment(_segmentsPath, checked(++_lastSegmentNumber));
            if (result == null)
                throw new InvalidOperationException("CreateSegment returned null");
            Debug.Assert(result.Number == _lastSegmentNumber);

            _segments.Add(result);
            _tailSegment = result;

            // Notify all waiters
            _segmentOperationsMonitor.PulseAll();

            return result;
        }

        /// <summary>
        /// Gets tail segment if it is available or try to create a new segement
        /// </summary>
        private DiskQueueSegment<T> GetTailSegmentSlow(int timeout, CancellationToken token)
        {
            using (var waiter = _segmentOperationsMonitor.Enter(timeout, token))
            {
                do
                {
                    Debug.Assert(_tailSegment != null);
                    Debug.Assert(_segments.Count > 0);
                    Debug.Assert(_tailSegment == _segments[_segments.Count - 1]);
                    Debug.Assert(_headSegment != null);
                    Debug.Assert(_segments.Contains(_headSegment));
                    Debug.Assert(_segments.TakeWhile(o => o != _headSegment).All(o => o.IsCompleted));
                    Debug.Assert(_segments.IndexOf(_headSegment) <= _segments.IndexOf(_tailSegment));

                    DiskQueueSegment<T> result = _tailSegment;
                    if (!result.IsFull)
                        return result;

                    if (_segments.Count < _maxSegmentCount)
                        return AllocateNewSegment();
                }
                while (waiter.Wait());
            }

            return null;
        }
        /// <summary>
        /// Gets the active head segment available for add
        /// </summary>
        /// <returns>Tail segment if succeeded or null otherwise</returns>
        private DiskQueueSegment<T> GetTailSegment(int timeout, CancellationToken token)
        {
            var result = _tailSegment;
            Debug.Assert(result != null);
            if (!result.IsFull)
                return result;

            return GetTailSegmentSlow(timeout, token);
        }


        /// <summary>
        /// Moves head to the next non-completed segment
        /// </summary>
        /// <returns>New head segment</returns>
        private DiskQueueSegment<T> MoveHeadToNonCompletedSegment()
        {
            Debug.Assert(_segmentOperationsMonitor.IsEntered());

            DiskQueueSegment<T> curHeadSegment = _headSegment;

            if (!curHeadSegment.IsCompleted)
                return curHeadSegment;

            if (curHeadSegment == _segments[_segments.Count - 1])
                return curHeadSegment;

            for (int i = 0; i < _segments.Count; i++)
            {
                if (!_segments[i].IsCompleted || i == _segments.Count - 1)
                {
                    var result = _segments[i];
                    _headSegment = result;

                    Debug.Assert(_segments.IndexOf(_headSegment) <= _segments.IndexOf(_tailSegment));
                    return result;
                }
            }

            Debug.Fail("Operation should be completed inside cycle");
            return _headSegment;
        }
        /// <summary>
        /// Get head segment if available or traverse the head forward
        /// </summary>
        private DiskQueueSegment<T> GetHeadSegmentSlow(int timeout, CancellationToken token)
        {
            using (var waiter = _segmentOperationsMonitor.Enter(timeout, token))
            {
                do
                {
                    Debug.Assert(_tailSegment != null);
                    Debug.Assert(_segments.Count > 0);
                    Debug.Assert(_tailSegment == _segments[_segments.Count - 1]);
                    Debug.Assert(_headSegment != null);
                    Debug.Assert(_segments.Contains(_headSegment));
                    Debug.Assert(_segments.TakeWhile(o => o != _headSegment).All(o => o.IsCompleted));
                    Debug.Assert(_segments.IndexOf(_headSegment) <= _segments.IndexOf(_tailSegment));

                    DiskQueueSegment<T> result = _headSegment;
                    if (!result.IsCompleted)
                        return result;

                    // Search for not completed segment
                    result = MoveHeadToNonCompletedSegment();
                    if (!result.IsCompleted)
                    {
                        // Perform compaction
                        if (!IsBackgroundCompactionEnabled && _headSegment != _segments[0])
                            Compact();

                        return result;
                    }
                }
                while (waiter.Wait());
            }

            return null;
        }

        /// <summary>
        /// Gets that active head segment available for take
        /// </summary>
        /// <returns>Head segment if succeeded or null otherwise</returns>
        private DiskQueueSegment<T> GetHeadSegment(int timeout, CancellationToken token)
        {
            var result = _headSegment;
            Debug.Assert(result != null);
            if (!result.IsCompleted)
                return result;

            return GetHeadSegmentSlow(timeout, token);
        }


        public override void AddForced(T item)
        {
            throw new NotImplementedException();
        }

        protected override bool TryAddCore(T item, int timeout, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        protected override bool TryTakeCore(out T item, int timeout, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        protected override bool TryPeekCore(out T item, int timeout, CancellationToken token)
        {
            throw new NotImplementedException();
        }


        // ============== Background compaction ===============

        /// <summary>
        /// Background compaction logic
        /// </summary>
        /// <param name="token">Cancellation Token</param>
        private void BackgroundCompactionProc(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!token.WaitHandle.WaitOne(CompactionPeriodMs))
                    Compact();
            }
        }


        /// <summary>
        /// Clean-up all resources
        /// </summary>
        /// <param name="isUserCall">Was called by user</param>
        protected override void Dispose(bool isUserCall)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;


            }
        }
    }
}
