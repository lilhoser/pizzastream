﻿/* 
Licensed to the Apache Software Foundation (ASF) under one
or more contributor license agreements.  See the NOTICE file
distributed with this work for additional information
regarding copyright ownership.  The ASF licenses this file
to you under the Apache License, Version 2.0 (the
"License"); you may not use this file except in compliance
with the License.  You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing,
software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
KIND, either express or implied.  See the License for the
specific language governing permissions and limitations
under the License.
*/
using System.Diagnostics;
using System.Net.Mail;
using System.Net.Mime;
using System.Net;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public class Alerter
    {
        private Dictionary<Guid, AlertEvent> m_AlertEvents;
        private Settings m_Settings;

        public Alerter(Settings Settings)
        {
            m_Settings = Settings;
            m_AlertEvents = new Dictionary<Guid, AlertEvent>();
        }

        public void ProcessAlerts(TranscribedCall Call)
        {
            Call.IsAlertMatch = false;
            foreach (var alert in m_Settings.Alerts)
            {
                Trace(TraceLoggerType.Alerts,
                      TraceEventType.Verbose,
                      $"Processing alert {alert.Name} for call ID {Call.CallId}");
                if (!alert.Enabled)
                {
                    Trace(TraceLoggerType.Alerts,
                          TraceEventType.Information,
                          $"Alert {alert.Name} is disabled, skipping.");
                    continue;
                }
                var keywords = alert.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var keyword in keywords)
                {
                    if (Call.Transcription.ToLower().Contains(keyword.ToLower()))
                    {
                        Trace(TraceLoggerType.Alerts,
                              TraceEventType.Information,
                              $"Alert {alert.Name} keyword {keyword} matches call ID {Call.CallId} transcription.");
                        Call.IsAlertMatch = true;                        
                        TriggerAlertEvent(alert, Call);
                        break;
                    }
                }
                if (Call.IsAlertMatch)
                {
                    break;
                }
            }
        }

        public static void ProcessAlertsOffline(TranscribedCall Call, List<Alert> Alerts)
        {
            Call.IsAlertMatch = false;
            foreach (var alert in Alerts)
            {
                Trace(TraceLoggerType.Alerts,
                      TraceEventType.Verbose,
                      $"Processing offline alert {alert.Name} for call ID {Call.CallId}");
                if (!alert.Enabled)
                {
                    Trace(TraceLoggerType.Alerts,
                          TraceEventType.Information,
                          $"Offline alert {alert.Name} is disabled, skipping.");
                    continue;
                }
                var keywords = alert.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var keyword in keywords)
                {
                    if (Call.Transcription.ToLower().Contains(keyword.ToLower()))
                    {
                        Trace(TraceLoggerType.Alerts,
                              TraceEventType.Information,
                              $"Offline alert {alert.Name} keyword {keyword} matches call ID {Call.CallId} transcription.");
                        Call.IsAlertMatch = true;
                        break;
                    }
                }
                if (Call.IsAlertMatch)
                {
                    break;
                }
            }
        }

        private void TriggerAlertEvent(Alert Alert, TranscribedCall Call)
        {
            //
            // If this alert has triggered before, pull its record, otherwise create
            // create a new event tracker.
            //
            AlertEvent alertEvent;
            if (m_AlertEvents.ContainsKey(Alert.Id))
            {
                alertEvent = m_AlertEvents[Alert.Id];
            }
            else
            {
                alertEvent = new AlertEvent(Alert.Id);
            }

            //
            // Because this function can be invoked by multiple threads processing different
            // calls, we must acquire the AlertEvent lock exclusive (writer).
            //
            alertEvent.LockExclusive();

            try
            {
                if (alertEvent.LastTriggered != DateTime.MinValue)
                {
                    int diff;
                    switch (Alert.Frequency)
                    {
                        case AlertFrequency.Daily:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalDays;
                                break;
                            }
                        case AlertFrequency.Hourly:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalHours;
                                break;
                            }
                        case AlertFrequency.RealTime:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalSeconds;
                                if (alertEvent.TriggerCountLastInterval > AlertEvent.s_RealtimeThresholdPerInterval)
                                {
                                    if (diff <= AlertEvent.s_RealtimeIntervalSec)
                                    {
                                        Trace(TraceLoggerType.Alerts,
                                              TraceEventType.Warning,
                                              $"NOT triggering: Alert set to real-time has been triggered " +
                                              $"{alertEvent.TriggerCountLastInterval} times in the past {diff} seconds.");
                                        return;
                                    }
                                    alertEvent.TriggerCountLastInterval = 0; // reset the count
                                }
                                diff = 1;
                                break;
                            }
                        default:
                            {
                                Trace(TraceLoggerType.Alerts,
                                      TraceEventType.Error,
                                      $"Unrecognized alert frequency {Alert.Frequency}");
                                return;
                            }
                    }

                    if (diff < 1)
                    {
                        Trace(TraceLoggerType.Alerts,
                              TraceEventType.Warning,
                              $"NOT triggering: Alert set to {Alert.Frequency}, last triggered " +
                              $"{alertEvent.LastTriggered:M/d/yyyy h:mm tt}");
                        return;
                    }
                }

                alertEvent.LastTriggered = DateTime.Now;
                alertEvent.TriggerCountLastInterval++;

                if (!string.IsNullOrEmpty(Alert.Email) &&
                    !string.IsNullOrEmpty(m_Settings.gmailUser) &&
                    !string.IsNullOrEmpty(m_Settings.gmailPassword))
                {
                    alertEvent.Unlock(); // don't hold lock, email could block
                    SendEmailNotification(Alert, alertEvent, Call);
                }
            }
            finally
            {
                alertEvent.Unlock();
            }
        }

        private void SendEmailNotification(Alert Alert, AlertEvent AlertEvent, TranscribedCall Call)
        {
            var formattedTalkgroup = TalkgroupHelper.FormatTalkgroup(m_Settings, Call.Talkgroup);
            var sender = new MailAddress(m_Settings.gmailUser!, "pizzawave notifications");
            string password = m_Settings.gmailPassword!;
            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(sender.Address, password),
                Timeout = 20000
            };
            var recipients = Alert.GetEmailRecipients();

            foreach (var recipient in recipients)
            {
                var recipientAddress = new MailAddress(recipient, null);
                using (var message = new MailMessage(sender, recipientAddress)
                {
                    Subject = $"pizzawave alert: {Alert.Name}",
                    IsBodyHtml = true,
                    Body = $"The following audio transcription from talkgroup <b>{formattedTalkgroup}" +
                        $"</b> has triggered your alert named <b>" +
                        $"{Alert.Name}</b> on <b>{AlertEvent.LastTriggered:M/d/yyyy h:mm tt}</b>:" +
                        $"<P><I>{Call.Transcription}</I></P>"
                })
                {
                    var contentType = new ContentType();
                    contentType.MediaType = MediaTypeNames.Application.Octet;
                    contentType.Name = Path.GetFileName(Call.Location);
                    message.Attachments.Add(new Attachment(Call.Location, contentType));
                    smtp.Send(message);
                }
            }
        }
    }
}
