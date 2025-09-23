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

// Persistent microsecond base aligned to epoch for dedup and replay
long   ULOG_BASE_US = 0;
bool   ULOG_BASE_READY = false;
const long ULOG_JSON_MAX_SAFE_INT = 9007199254740991; // 2^53-1 to keep JSON numbers precise

void ULogRefreshBase()
{
   long currentSeconds = (long)TimeCurrent();
   const long microsPerSecond = (long)1000000;
   long microOffset = (long)GetMicrosecondCount();
   ULOG_BASE_US = currentSeconds * microsPerSecond - microOffset;
   ULOG_BASE_READY = true;
}

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

   if(!ULOG_BASE_READY)
   {
      ULogRefreshBase();
   }

   string key = level + "|" + message;
   long now_us = ULOG_BASE_US + (long)GetMicrosecondCount();
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
   ULogRefreshBase();
}

void ULogPush(const string level, const string message, const string base_id)
{
   if(!_ULogShouldEmit(level, message)) return;
   if(ULOG_COUNT >= ULOG_MAX) return;
   // Use epoch-based nanoseconds for consistent ordering with bridge/server logs
   // Note: TimeCurrent() returns seconds since 1970-01-01 (UTC). Multiply to ns in 64-bit.
   long epochSeconds = (long)TimeCurrent();
   const long nanosPerSecond = (long)1000000000;
   long epoch_ns = epochSeconds * nanosPerSecond;
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

// JSON helpers ---------------------------------------------------------
string _ULogJsonEscape(const string value)
{
   string result = "";
   const int len = StringLen(value);
   for(int i = 0; i < len; i++)
   {
      const ushort ch = StringGetCharacter(value, i);
      if(ch == '\\')
      {
         result += "\\\\";
      }
      else if(ch == '\"')
      {
         result += "\\\"";
      }
      else if(ch == 0x08)
      {
         result += "\\b";
      }
      else if(ch == 0x0C)
      {
         result += "\\f";
      }
      else if(ch == 0x0A)
      {
         result += "\\n";
      }
      else if(ch == 0x0D)
      {
         result += "\\r";
      }
      else if(ch == 0x09)
      {
         result += "\\t";
      }
      else if(ch < 0x20 || ch > 0x7F)
      {
         result += StringFormat("\\u%04X", ch);
      }
      else
      {
         result += StringSubstr(value, i, 1);
      }
   }
   return result;
}

void _ULogJsonAppendString(string &json, const string key, const string value, bool &first)
{
   if(!first)
      json += ",";
   json += "\"";
   json += _ULogJsonEscape(key);
   json += "\":"";
   json += _ULogJsonEscape(value);
   json += "\"";
   first = false;
}

void _ULogJsonAppendRaw(string &json, const string key, const string rawValue, bool &first)
{
   if(!first)
      json += ",";
   json += "\"";
   json += _ULogJsonEscape(key);
   json += "\":";
   json += rawValue;
   first = false;
}

void _ULogJsonAppendInt(string &json, const string key, const long value, bool &first)
{
   string asString = StringFormat("%I64d", value);
   _ULogJsonAppendRaw(json, key, asString, first);
}

bool _ULogValidateJson(const string json)
{
   const int len = StringLen(json);
   if(len < 2)
      return false;

   bool inString = false;
   bool escape = false;
   int braceDepth = 0;

   for(int i = 0; i < len; i++)
   {
      const ushort ch = StringGetCharacter(json, i);
      if(escape)
      {
         escape = false;
         continue;
      }

      if(ch == '\\')
      {
         escape = true;
         continue;
      }

      if(ch == '"')
      {
         inString = !inString;
         continue;
      }

      if(!inString)
      {
         if(ch == '{')
            braceDepth++;
         else if(ch == '}')
         {
            braceDepth--;
            if(braceDepth < 0)
               return false;
         }
      }
   }

   return !inString && braceDepth == 0 && StringGetCharacter(json, 0) == '{' && StringGetCharacter(json, len - 1) == '}';
}

string _ULogBuildLogJson(const ULogEvent event)
{
   bool first = true;
   string json = "{";

   _ULogJsonAppendInt(json, "timestamp_ns", event.ts_ns, first);
   _ULogJsonAppendString(json, "source", "mt5", first);
   _ULogJsonAppendString(json, "level", event.level, first);
   _ULogJsonAppendString(json, "component", "EA", first);
   _ULogJsonAppendString(json, "message", event.message, first);
   _ULogJsonAppendString(json, "base_id", event.base_id, first);

   if(event.instrument != "")
      _ULogJsonAppendString(json, "instrument", event.instrument, first);

   if(event.mt5_ticket > 0)
   {
      if(event.mt5_ticket > ULOG_JSON_MAX_SAFE_INT)
         _ULogJsonAppendString(json, "mt5_ticket", StringFormat("%I64d", event.mt5_ticket), first);
      else
         _ULogJsonAppendInt(json, "mt5_ticket", event.mt5_ticket, first);
   }

   if(event.error_code != "")
      _ULogJsonAppendString(json, "error_code", event.error_code, first);

   _ULogJsonAppendString(json, "schema_version", "mt5-1", first);

   json += "}";
   if(!_ULogValidateJson(json))
      return "";

   return json;
}

// Flush buffered log events to the Bridge via GrpcLog()
int ULogFlush()
{
   int sent = 0;
   int attempted = ULOG_COUNT;
   for(int i=0; i<ULOG_COUNT; i++)
   {
      ULogEvent event = ULOG_BUFFER[i];
      string json = _ULogBuildLogJson(event);
      if(json == \"\")
      {
         Print("ULogFlush: Skipping malformed log event level=", event.level, ", msg=", event.message);
         continue;
      }

      int rc = GrpcLog(json);
      if(rc == 0) {
         sent++;
      } else {
         // Surface failures in Experts tab for troubleshooting
         Print("ULogFlush: GrpcLog failed rc=", rc, ", level=", event.level, ", msg=", event.message);
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
