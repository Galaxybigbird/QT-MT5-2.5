#if false
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Quantower.MultiStrat.Utilities;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat
{
    public partial class MultiStratPlugin
    {
        private const int MaxLogBuffer = 5000;
        private object? _browser;
        private bool _browserBridgeAttached;

        private static object? TryGetValue(IDictionary<string, object?> payload, string key)
        {
            if (payload == null)
            {
                return null;
            }

            payload.TryGetValue(key, out var value);
            return value;
        }

        private void AttachBrowserCommandBridge()
        {
            try
            {
                var browser = _browser;
                if (browser == null || _browserBridgeAttached)
                {
                    return;
                }

                foreach (var ev in browser.GetType().GetEvents())
                {
                    var name = ev.Name ?? string.Empty;
                    bool looksLikeNav = name.IndexOf("Navig", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("NewWindow", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Address", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Url", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Location", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!looksLikeNav)
                    {
                        continue;
                    }

                    var handlerType = ev.EventHandlerType;
                    if (handlerType == typeof(EventHandler))
                    {
                        var method = GetType().GetMethod(nameof(OnBrowserNavEvent), BindingFlags.NonPublic | BindingFlags.Instance);
                        if (method != null)
                        {
                            var del = Delegate.CreateDelegate(handlerType, this, method);
                            ev.AddEventHandler(browser, del);
                            AddLogEntry("INFO", $"Attached browser handler: {ev.Name}");
                            continue;
                        }
                    }
                    else if (handlerType != null && handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>))
                    {
                        var argType = handlerType.GetGenericArguments()[0];
                        var methodGeneric = GetType().GetMethod(nameof(OnBrowserNavGeneric), BindingFlags.NonPublic | BindingFlags.Instance);
                        if (methodGeneric != null)
                        {
                            var method = methodGeneric.MakeGenericMethod(argType);
                            var del = Delegate.CreateDelegate(handlerType, this, method);
                            ev.AddEventHandler(browser, del);
                            AddLogEntry("INFO", $"Attached browser handler: {ev.Name}<{argType.Name}>");
                            continue;
                        }
                    }
                }

                _browserBridgeAttached = true;
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to attach Browser command bridge: {ex.Message}");
            }
        }

        private void HandleMsbCommand(string url)
        {
            try
            {
                var uri = new Uri(url);
                var cmd = uri.Host?.Trim().ToLowerInvariant() ?? string.Empty;

                string? data = null;
                var query = uri.Query;
                if (!string.IsNullOrEmpty(query))
                {
                    var raw = query.StartsWith("?") ? query.Substring(1) : query;
                    foreach (var part in raw.Split('&'))
                    {
                        if (string.IsNullOrEmpty(part))
                        {
                            continue;
                        }

                        var separator = part.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        if (!string.Equals(part.Substring(0, separator), "d", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        data = Uri.UnescapeDataString(part.Substring(separator + 1));
                        break;
                    }
                }

                var payload = new Dictionary<string, object?>();
                if (!string.IsNullOrEmpty(data))
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(data)));
                        payload = SimpleJson.DeserializeObject<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                    }
                    catch
                    {
                        // ignore malformed payloads
                    }
                }

                switch (cmd)
                {
                    case "connect":
                        if (payload.TryGetValue("address", out var addrObj) && addrObj is string address && !string.IsNullOrWhiteSpace(address))
                        {
                            _ = _managerService.ConnectAsync(address).ContinueWith(t =>
                            {
                                void Update()
                                {
                                    PushStatusToBrowser();
                                    if (t.IsFaulted)
                                    {
                                        AddLogEntry("ERROR", $"Connect command failed: {t.Exception?.GetBaseException().Message}");
                                    }
                                }

                                if (_dispatcher != null && !_dispatcher.CheckAccess())
                                {
                                    _dispatcher.BeginInvoke((Action)Update, DispatcherPriority.Background);
                                }
                                else if (_uiContext != null)
                                {
                                    _uiContext.Post(_ => Update(), null);
                                }
                                else
                                {
                                    Update();
                                }
                            }, TaskScheduler.Default);
                        }

                        break;
                    case "flatten_all":
                    {
                        var disable = payload.TryGetValue("disableAfter", out var disObj) && disObj is bool flag && flag;
                        _ = _managerService.FlattenAllAsync(disable, "Quantower UI flatten");
                        break;
                    }
                    case "reset_daily":
                    {
                        _managerService.ResetDailyRisk(null);

                        void Update()
                        {
                            PushStatusToBrowser();
                        }

                        if (_dispatcher != null && !_dispatcher.CheckAccess())
                        {
                            _dispatcher.BeginInvoke((Action)Update, DispatcherPriority.Background);
                        }
                        else if (_uiContext != null)
                        {
                            _uiContext.Post(_ => Update(), null);
                        }
                        else
                        {
                            Update();
                        }

                        break;
                    }
                    case "update_trailing":
                    {
                        Services.TrailingElasticService.ProfitUnitType ParseUnit(object? value)
                        {
                            if (value is string s && Enum.TryParse<Services.TrailingElasticService.ProfitUnitType>(s, true, out var parsed))
                            {
                                return parsed;
                            }

                            if (value is int i && Enum.IsDefined(typeof(Services.TrailingElasticService.ProfitUnitType), i))
                            {
                                return (Services.TrailingElasticService.ProfitUnitType)i;
                            }

                            return Services.TrailingElasticService.ProfitUnitType.Dollars;
                        }

                        double ReadDouble(object? value) => value is IConvertible convertible
                            ? Convert.ToDouble(convertible, CultureInfo.InvariantCulture)
                            : 0d;

                        int ReadInt(object? value) => value is IConvertible convertible
                            ? Convert.ToInt32(convertible, CultureInfo.InvariantCulture)
                            : 0;

                        var update = new MultiStratManagerService.TrailingSettingsUpdate(
                            EnableElastic: payload.TryGetValue("enable_elastic", out var ee) && ee is bool be && be,
                            ElasticTriggerUnits: ParseUnit(TryGetValue(payload, "elastic_trigger_units")),
                            ProfitUpdateThreshold: ReadDouble(TryGetValue(payload, "profit_update_threshold")),
                            ElasticIncrementUnits: ParseUnit(TryGetValue(payload, "elastic_increment_units")),
                            ElasticIncrementValue: ReadDouble(TryGetValue(payload, "elastic_increment_value")),
                            EnableTrailing: payload.TryGetValue("enable_trailing", out var et) && et is bool bt && bt,
                            UseDemaAtrTrailing: payload.TryGetValue("enable_dema_atr_trailing", out var da) && da is bool bda && bda,
                            TrailingActivationUnits: ParseUnit(TryGetValue(payload, "trailing_activation_units")),
                            TrailingActivationValue: ReadDouble(TryGetValue(payload, "trailing_activation_value")),
                            TrailingStopUnits: ParseUnit(TryGetValue(payload, "trailing_stop_units")),
                            TrailingStopValue: ReadDouble(TryGetValue(payload, "trailing_stop_value")),
                            DemaAtrMultiplier: ReadDouble(TryGetValue(payload, "dema_atr_multiplier")),
                            AtrPeriod: Math.Max(1, ReadInt(TryGetValue(payload, "atr_period"))),
                            DemaPeriod: Math.Max(1, ReadInt(TryGetValue(payload, "dema_period"))));

                        _managerService.UpdateTrailingSettings(update);
                        break;
                    }
                    case "window":
                        if (payload.TryGetValue("action", out var actObj) && actObj is string action)
                        {
                            SetWindowAction(action);
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to handle msb command: {ex.Message}");
            }
        }

        private void PushStatusToBrowser()
        {
            try
            {
                if (_browser == null)
                {
                    return;
                }

                var payload = new Dictionary<string, object?>
                {
                    ["connected"] = _managerService.IsConnected,
                    ["address"] = _managerService.CurrentAddress ?? string.Empty,
                };

                var json = SimpleJson.SerializeObject(payload);
                TryBrowserInvokeJs($"window.MSB && MSB.setStatus({json})");
            }
            catch
            {
                // ignore
            }
        }

        private void TryBrowserInvokeJs(string script)
        {
            try
            {
                if (_browser == null || string.IsNullOrWhiteSpace(script))
                {
                    return;
                }

                var method = _browser.GetType().GetMethod("UpdateHtml", new[] { typeof(string), typeof(HtmlAction), typeof(string) });
                method?.Invoke(_browser, new object[] { string.Empty, HtmlAction.InvokeJs, script });
            }
            catch
            {
                // ignore browser invocation failures
            }
        }
    }
}

#endif
