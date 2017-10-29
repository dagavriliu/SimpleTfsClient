using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace TfsClient
{
    public class ExtendedWorkItem : WorkItem
    {
        [JsonIgnore]
        public IEnumerable<ExtendedWorkItem> Children { get; set; } = new List<ExtendedWorkItem>();

        [JsonIgnore]
        public IEnumerable<ExtendedWorkItem> Parents { get; set; } = new List<ExtendedWorkItem>();

        private Dictionary<string, object> _parsed = new Dictionary<string, object> { };

        private T Get<T>(string key, Func<object, T> convert)
        {
            if (_parsed.ContainsKey(key))
            {
                return (T)_parsed[key];
            }

            if (Fields.ContainsKey(key))
            {
                var converted = convert(Fields[key]);
                _parsed.Add(key, converted);
                return converted;
            }
            else
            {
                _parsed.Add(key, null);
            }
            return default(T);
        }

        [JsonIgnore]
        public string ItemType { get { return Get("System.WorkItemType", o => o as string); } }

        [JsonIgnore]
        public string Title { get { return Get("System.Title", o => o as string); } }

        [JsonIgnore]
        public string AssignedTo { get { return Get("System.AssignedTo", o => o as string); } }

        private DateTime? ParseDateTime(object o)
        {
            DateTime result;
            if (DateTime.TryParse(string.Empty + o, out result))
            {
                return result;
            }
            return null;
        }

        [JsonIgnore]
        public DateTime? ClosedDate { get { return Get("Microsoft.VSTS.Common.ClosedDate", ParseDateTime); } }

        [JsonIgnore]
        public DateTime? CreatedDate { get { return Get("System.CreatedDate", ParseDateTime); } }

    }


}
