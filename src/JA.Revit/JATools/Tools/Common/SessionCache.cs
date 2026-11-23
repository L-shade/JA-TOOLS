using System;
using System.Collections.Generic;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Values computed once per tool run and reused (wall faces, room boundaries, view names...).
    /// ToolCommand clears it when a tool starts and when it ends, so nothing survives from one run
    /// to the next. Entries that depend on geometry carry a "version" in their key (wall width and
    /// ends, room area), so a change made during the same run gives a new entry, never a stale one.
    /// </summary>
    public static class SessionCache
    {
        private static readonly Dictionary<string, object> Items = new Dictionary<string, object>();

        public static void Clear()
        {
            lock (Items) Items.Clear();
        }

        public static T Get<T>(string key, Func<T> make)
        {
            lock (Items)
            {
                if (Items.TryGetValue(key, out object v) && v is T t) return t;
            }
            T made = make();
            lock (Items) Items[key] = made;
            return made;
        }

        public static bool TryGet<T>(string key, out T value)
        {
            lock (Items)
            {
                if (Items.TryGetValue(key, out object v) && v is T t) { value = t; return true; }
            }
            value = default(T);
            return false;
        }

        public static void Set(string key, object value)
        {
            lock (Items) Items[key] = value;
        }

        public static void Remove(string key)
        {
            lock (Items) Items.Remove(key);
        }
    }
}
