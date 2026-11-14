using System;
using System.Collections.Generic;
using osu.Framework.Graphics;

namespace osu.Game.Graphics.Containers
{
    /// <summary>
    /// Spreads attachment of preloaded drawable trees over update frames. The owner calls
    /// AttachNext once per frame; abandoned, unattached trees are disposed on replacement.
    /// </summary>
    public sealed class FrameStagedDrawableQueue<T> : IDisposable where T : Drawable
    {
        private readonly Queue<T> pending = new Queue<T>();
        public int Count => pending.Count;
        public IEnumerable<T> PendingItems => pending;

        public void Replace(IEnumerable<T> items)
        {
            Dispose();
            foreach (var item in items) pending.Enqueue(item);
        }

        /// <summary>
        /// Appends trees after the ones still pending (for paged lists, where earlier pages must not be dropped).
        /// </summary>
        public void Enqueue(IEnumerable<T> items)
        {
            foreach (var item in items) pending.Enqueue(item);
        }

        public void AttachNext(Action<T> attach)
        {
            if (pending.TryDequeue(out var item)) attach(item);
        }

        public void Dispose()
        {
            while (pending.TryDequeue(out var item)) item.Dispose();
        }
    }
}
