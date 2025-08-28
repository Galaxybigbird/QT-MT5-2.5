// Include guard for MQL5 header
#ifndef __UNIFIED_LOGGING_MQH__
#define __UNIFIED_LOGGING_MQH__

// Minimal unified logging buffer for MT5 (skeleton)
struct ULogEvent
{
   string level;
   string message;
   string base_id;
   // Optional rich diagnostics
   string instrument;
   long   mt5_ticket;
   string error_code;
   long   ts_ns;
};

int   ULOG_MAX = 256;
ULogEvent ULOG_BUFFER[];
int   ULOG_COUNT = 0;

// Default current base id used by macros; EA can update as needed
string ULOG_CURRENT_BASE_ID = "";

// Optional context carried into each log event until cleared
string ULOG_CTX_INSTRUMENT = "";
long   ULOG_CTX_MT5_TICKET = 0;
string ULOG_CTX_ERROR_CODE = "";

// Context setters/clearers (call before emitting logs)
void ULogSetInstrument(const string instrument) { ULOG_CTX_INSTRUMENT = instrument; }
void ULogClearInstrument() { ULOG_CTX_INSTRUMENT = ""; }
void ULogSetMt5Ticket(const long ticket) { ULOG_CTX_MT5_TICKET = ticket; }
void ULogClearMt5Ticket() { ULOG_CTX_MT5_TICKET = 0; }
void ULogSetErrorCode(const string code) { ULOG_CTX_ERROR_CODE = code; }
void ULogClearErrorCode() { ULOG_CTX_ERROR_CODE = ""; }

// Duplicate suppression (per message+level) to avoid spamming unchanged logs
int   ULOG_DEDUP_SIZE = 32;
int   ULOG_DEDUP_WINDOW_US = 2000000; // 2 seconds window
string ULOG_DEDUP_MSGS[];
long   ULOG_DEDUP_LAST_US[];
int    ULOG_DEDUP_COUNTS[];

bool _ULogShouldEmit(const string level, const string message)
{
   static bool _init = false;
   if(!_init)
   {
      ArrayResize(ULOG_DEDUP_MSGS, ULOG_DEDUP_SIZE);
      ArrayResize(ULOG_DEDUP_LAST_US, ULOG_DEDUP_SIZE);
      ArrayResize(ULOG_DEDUP_COUNTS, ULOG_DEDUP_SIZE);
      for(int i=0;i<ULOG_DEDUP_SIZE;i++){ ULOG_DEDUP_MSGS[i]=""; ULOG_DEDUP_LAST_US[i]=0; ULOG_DEDUP_COUNTS[i]=0; }
      _init = true;
   }

   string key = level + "|" + message;
   long now_us = (long)GetMicrosecondCount();
   int freeIdx = -1;
   int oldestIdx = 0;
   long oldestTs = ULOG_DEDUP_LAST_US[0];
   for(int i=0;i<ULOG_DEDUP_SIZE;i++)
   {
      if(ULOG_DEDUP_MSGS[i] == key)
      {
         if(now_us - ULOG_DEDUP_LAST_US[i] < ULOG_DEDUP_WINDOW_US)
         {
            ULOG_DEDUP_COUNTS[i]++;
            return false; // suppress duplicate within window
         }
         // outside window: allow emit and refresh timestamp
         ULOG_DEDUP_LAST_US[i] = now_us;
         ULOG_DEDUP_COUNTS[i] = 0;
         return true;
      }
      if(ULOG_DEDUP_MSGS[i] == "" && freeIdx == -1) freeIdx = i;
      if(ULOG_DEDUP_LAST_US[i] < oldestTs){ oldestTs = ULOG_DEDUP_LAST_US[i]; oldestIdx = i; }
   }
   int idx = (freeIdx != -1 ? freeIdx : oldestIdx);
   ULOG_DEDUP_MSGS[idx] = key;
   ULOG_DEDUP_LAST_US[idx] = now_us;
   ULOG_DEDUP_COUNTS[idx] = 0;
   return true;
}

void ULogInit()
{
   ArrayResize(ULOG_BUFFER, ULOG_MAX);
   ULOG_COUNT = 0;
}

void ULogPush(const string level, const string message, const string base_id)
{
   if(!_ULogShouldEmit(level, message)) return;
   if(ULOG_COUNT >= ULOG_MAX) return;
   // Use epoch-based nanoseconds for consistent ordering with bridge/server logs
   // Note: TimeCurrent() returns seconds since 1970-01-01 (UTC). Multiply to ns in 64-bit.
   long epoch_ns = ((long)TimeCurrent()) * (long)1000000000;
   ULogEvent e;
   e.level = level;
   e.message = message;
   e.base_id = base_id;
   // Copy optional context into the event snapshot
   e.instrument = ULOG_CTX_INSTRUMENT;
   e.mt5_ticket = ULOG_CTX_MT5_TICKET;
   e.error_code = ULOG_CTX_ERROR_CODE;
   e.ts_ns = epoch_ns;
   ULOG_BUFFER[ULOG_COUNT++] = e;
}

#define ULOG_INFO(msg)  ULogPush("INFO",  msg, ULOG_CURRENT_BASE_ID)
#define ULOG_WARN(msg)  ULogPush("WARN",  msg, ULOG_CURRENT_BASE_ID)
#define ULOG_ERROR(msg) ULogPush("ERROR", msg, ULOG_CURRENT_BASE_ID)

// Helpers that mirror to Experts Print and also push to unified log buffer
// Note: Do not gate twice. Dedup happens in ULogPush; we always Print here.
void ULogInfoPrint(const string msg)
{
   Print(msg);
   ULOG_INFO(msg);
}

void ULogWarnPrint(const string msg)
{
   Print(msg);
   ULOG_WARN(msg);
}

void ULogErrorPrint(const string msg)
{
   Print(msg);
   ULOG_ERROR(msg);
}

// Import logging from the native C++ gRPC client DLL to share the same client state
#import "MT5GrpcClientNative.dll"
   int GrpcLog(string log_json);
#import

// Simple JSON string escaper for quotes and backslashes
string _ULogJsonEscape(const string s)
{
   string out = s;
   StringReplace(out, "\\", "\\\\");
   StringReplace(out, "\"", "\\\"");
   StringReplace(out, "\n", "\\n");
   StringReplace(out, "\r", "\\r");
   return out;
}

// Flush buffered log events to the Bridge via GrpcLog()
int ULogFlush()
{
   int sent = 0;
   int attempted = ULOG_COUNT;
   for(int i=0; i<ULOG_COUNT; i++)
   {
   string msgEsc = _ULogJsonEscape(ULOG_BUFFER[i].message);
   string baseEsc = _ULogJsonEscape(ULOG_BUFFER[i].base_id);
   // Build JSON with optional fields instrument, mt5_ticket, error_code
   string json = StringFormat("{\"timestamp_ns\":%I64d,\"source\":\"mt5\",\"level\":\"%s\",\"component\":\"EA\",\"message\":\"%s\",\"base_id\":\"%s\"", ULOG_BUFFER[i].ts_ns, ULOG_BUFFER[i].level, msgEsc, baseEsc);
   if(ULOG_BUFFER[i].instrument != "")
   {
      string instEsc = _ULogJsonEscape(ULOG_BUFFER[i].instrument);
      // Append optional instrument field
      json += StringFormat(",\"instrument\":\"%s\"", instEsc);
   }
   if(ULOG_BUFFER[i].mt5_ticket > 0)
   {
      // Append numeric mt5_ticket as JSON number using 64-bit integer format
      json += StringFormat(",\"mt5_ticket\":%I64d", (long)ULOG_BUFFER[i].mt5_ticket);
   }
   if(ULOG_BUFFER[i].error_code != "")
   {
      string ecEsc = _ULogJsonEscape(ULOG_BUFFER[i].error_code);
      json += StringFormat(",\"error_code\":\"%s\"", ecEsc);
   }
   json += ",\"schema_version\":\"mt5-1\"}";
      int rc = GrpcLog(json);
      if(rc == 0) {
         sent++;
      } else {
         // Surface failures in Experts tab for troubleshooting
         Print("ULogFlush: GrpcLog failed rc=", rc, ", level=", ULOG_BUFFER[i].level, ", msg=", ULOG_BUFFER[i].message);
      }
   }
   ULOG_COUNT = 0;
   // Throttle flush summaries to avoid spam
   static long _last_flush_us = 0;
   long now_us = (long)GetMicrosecondCount();
   if(attempted > 0 && (now_us - _last_flush_us >= 3000000 || attempted >= 32)) // every 3s or large batch
   {
      Print("ULogFlush: attempted=", attempted, ", sent=", sent);
      _last_flush_us = now_us;
   }
   return sent;
}

// Optional auto-flush helper with simple throttling (flush at most ~2 Hz or when buffer is large)
void ULogAutoFlush()
{
   static long _last_us = 0;
   long now_us = (long)GetMicrosecondCount();
   // Flush if more than 500ms passed or buffer grew large
   if(ULOG_COUNT >= 16 || (now_us - _last_us) >= 500000)
   {
      if(ULOG_COUNT > 0)
      {
         ULogFlush();
      }
      _last_us = now_us;
   }
}

// Note: EA should call ULogFlush() periodically (e.g., in OnTimer or after key events)

#endif // __UNIFIED_LOGGING_MQH__
