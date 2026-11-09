// SQLite compiled with -pthread for the multithreaded wasm runtime (replaces SQLitePCLRaw's single-threaded e_sqlite3.a)
#define SQLITE_THREADSAFE 1
#define SQLITE_DEFAULT_MEMSTATUS 0
#define SQLITE_TEMP_STORE 3
#define SQLITE_ENABLE_FTS5 1
#define SQLITE_ENABLE_SNAPSHOT 1
#define SQLITE_ENABLE_COLUMN_METADATA 1
#include "sqlite/sqlite3.c"
// encryption entry points referenced by SQLitePCLRaw (unused, no SEE)
int sqlite3_key(sqlite3 *db, const void *k, int n) { return SQLITE_ERROR; }
int sqlite3_key_v2(sqlite3 *db, const char *z, const void *k, int n) { return SQLITE_ERROR; }
int sqlite3_rekey(sqlite3 *db, const void *k, int n) { return SQLITE_ERROR; }
int sqlite3_rekey_v2(sqlite3 *db, const char *z, const void *k, int n) { return SQLITE_ERROR; }
