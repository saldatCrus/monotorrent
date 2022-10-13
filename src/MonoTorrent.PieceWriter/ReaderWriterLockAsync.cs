//
// FileStreamBuffer.cs
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

using ReusableTasks;

namespace MonoTorrent.PieceWriter
{
    class ReaderWriterLockAsync
    {
        public readonly struct Releaser : IDisposable
        {
            ReaderWriterLockAsync? Locker { get; }
            bool ReadLockHeld { get; }

            public Releaser (ReaderWriterLockAsync locker, bool readLockHeld)
            {
                Locker = locker;
                ReadLockHeld = readLockHeld;
            }

            public void Dispose ()
            {
                if (ReadLockHeld)
                    Locker?.ExitReaderLockAsync ();
                else
                    Locker?.ExitWriterLockAsync ();
            }
        }

        readonly Queue<ReusableTaskCompletionSource<object?>> cache = new Queue<ReusableTaskCompletionSource<object?>> ();

        readonly object sync;
        readonly Queue<ReusableTaskCompletionSource<object?>> pendingReaders;
        readonly Queue<ReusableTaskCompletionSource<object?>> pendingWriters;

        int activeReaders;
        bool readersFirst;

        public ReaderWriterLockAsync ()
        {
            pendingReaders = new Queue<ReusableTaskCompletionSource<object?>> ();
            pendingWriters = new Queue<ReusableTaskCompletionSource<object?>> ();
            sync = new object ();
        }

        public async ReusableTask<Releaser> EnterReaderLockAsync ()
        {
            ReusableTaskCompletionSource<object?>? tcs = null;
            while (true) {
                lock (sync) {
                    if (pendingWriters.Count > 0 || activeReaders == -1) {
                        if (tcs is null)
                            lock (cache)
                                tcs = cache.Count > 0 ? cache.Dequeue () : new ReusableTaskCompletionSource<object?> (true);
                        pendingReaders.Enqueue (tcs);
                    } else {
                        activeReaders++;
                        if (tcs != null)
                            lock (cache)
                                cache.Enqueue (tcs);
                        return new Releaser (this, true);
                    }
                }
                if (tcs != null)
                    await tcs.Task;
            }
        }

        public async ReusableTask<Releaser> EnterWriterLockAsync ()
        {
            ReusableTaskCompletionSource<object?>? tcs = null;

            while (true) {
                lock (sync) {
                    if (activeReaders == 0) {
                        activeReaders = -1;
                        if (tcs != null)
                            lock (cache)
                                cache.Enqueue (tcs);
                        return new Releaser (this, readLockHeld: false);
                    } else {
                        if (tcs == null)
                            lock (cache)
                                tcs = cache.Count > 0 ? cache.Dequeue () : new ReusableTaskCompletionSource<object?> (true);
                        pendingWriters.Enqueue (tcs);
                    }
                }

                if (tcs != null)
                    await tcs.Task;
            }
        }

        void ExitReaderLockAsync ()
        {
            lock (sync) {
                activeReaders--;
                if (activeReaders == 0 && pendingWriters.Count > 0)
                    pendingWriters.Dequeue ().SetResult (null);
            }
        }

        void ExitWriterLockAsync ()
        {
            lock (sync) {
                activeReaders++;
                if (activeReaders == 0) {
                    if (!readersFirst && pendingWriters.Count > 0)
                        pendingWriters.Dequeue ().SetResult (null);

                    while (pendingReaders.Count > 0)
                        pendingReaders.Dequeue ().SetResult (null);

                    if (readersFirst && pendingWriters.Count > 0)
                        pendingWriters.Dequeue ().SetResult (null);

                    readersFirst = !readersFirst;
                }
            }
        }
    }
}
