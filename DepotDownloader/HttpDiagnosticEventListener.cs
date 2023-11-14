using System;
using System.Diagnostics.Tracing;
using System.Text;

namespace DepotDownloader
{
    internal sealed class HttpDiagnosticEventListener : EventListener
    {
        public const EventKeywords TasksFlowActivityIds = (EventKeywords)0x80;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.Http" ||
                eventSource.Name == "System.Net.Sockets" ||
                eventSource.Name == "System.Net.Security" ||
                eventSource.Name == "System.Net.NameResolution")
            {
                EnableEvents(eventSource, EventLevel.LogAlways);
            }
            else if (eventSource.Name == "System.Threading.Tasks.TplEventSource")
            {
                EnableEvents(eventSource, EventLevel.LogAlways, TasksFlowActivityIds);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            var sb = new StringBuilder().Append($"{eventData.TimeStamp:HH:mm:ss.fffffff}  {eventData.EventSource.Name}.{eventData.EventName}(");
            for (var i = 0; i < eventData.Payload?.Count; i++)
            {
                sb.Append(eventData.PayloadNames?[i]).Append(": ").Append(eventData.Payload[i]);
                if (i < eventData.Payload?.Count - 1)
                {
                    sb.Append(", ");
                }
            }

            sb.Append(')');
            Console.WriteLine(sb.ToString());
        }
    }
}
