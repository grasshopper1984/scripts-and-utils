using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentRoom
{
    internal sealed class Database : IDisposable
    {
        private IntPtr handle;
        internal Database(string path)
        {
            int result = sqlite3_open_v2(Utf8(path), out handle, 0x00000002 | 0x00000004 | 0x00010000, IntPtr.Zero);
            if (result != 0) { Dispose(); throw new RoomError("storage_error", "Cannot open the local room database."); }
            sqlite3_busy_timeout(handle, 3000);
            Execute("PRAGMA foreign_keys=ON");
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA synchronous=FULL");
        }
        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
        private IntPtr Prepare(string sql, object[] values)
        {
            IntPtr statement;
            if (sqlite3_prepare_v2(handle, Utf8(sql), -1, out statement, IntPtr.Zero) != 0)
                throw new RoomError("storage_error", "Database statement could not be prepared.");
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    int code;
                    object value = values[i];
                    if (value == null) code = sqlite3_bind_null(statement, i + 1);
                    else if (value is string)
                    {
                        byte[] bytes = Utf8((string)value);
                        code = sqlite3_bind_text(statement, i + 1, bytes, bytes.Length - 1, new IntPtr(-1));
                    }
                    else code = sqlite3_bind_int64(statement, i + 1, Convert.ToInt64(value));
                    if (code != 0) throw new RoomError("storage_error", "Database parameter could not be bound.");
                }
                return statement;
            }
            catch { sqlite3_finalize(statement); throw; }
        }
        internal List<Dictionary<string, object>> Query(string sql, params object[] values)
        {
            IntPtr statement = Prepare(sql, values);
            try
            {
                var rows = new List<Dictionary<string, object>>();
                int code;
                while ((code = sqlite3_step(statement)) == 100)
                {
                    var row = new Dictionary<string, object>();
                    for (int i = 0; i < sqlite3_column_count(statement); i++)
                    {
                        string name = Marshal.PtrToStringAnsi(sqlite3_column_name(statement, i));
                        int type = sqlite3_column_type(statement, i);
                        object value;
                        if (type == 5) value = null;
                        else if (type == 1) value = sqlite3_column_int64(statement, i);
                        else
                        {
                            int count = sqlite3_column_bytes(statement, i);
                            byte[] bytes = new byte[count];
                            if (count != 0) Marshal.Copy(sqlite3_column_text(statement, i), bytes, 0, count);
                            value = Encoding.UTF8.GetString(bytes);
                        }
                        row.Add(name, value);
                    }
                    rows.Add(row);
                }
                if (code != 101) throw new RoomError(code == 19 ? "conflict" : "storage_error",
                    code == 19 ? "A database uniqueness or consistency rule rejected this operation." : "Database operation did not complete.");
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }
        internal void Execute(string sql, params object[] values) { Query(sql, values); }
        internal Dictionary<string, object> One(string sql, params object[] values)
        {
            var rows = Query(sql, values);
            return rows.Count == 0 ? null : rows[0];
        }
        internal long LastId { get { return sqlite3_last_insert_rowid(handle); } }
        public void Dispose() { if (handle != IntPtr.Zero) { sqlite3_close_v2(handle); handle = IntPtr.Zero; } }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close_v2(IntPtr db);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int count, out IntPtr statement, IntPtr tail);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_last_insert_rowid(IntPtr db);
    }
}
