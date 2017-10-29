using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TfsClient
{
    public class VssConnectionDetails
    {
        public string CollectionUrl { get; set; }
        public string ProjectName { get; set; }
        public string PersonalAccessToken { get; set; }
        public string EditItemUriFormat { get; set; }
        public Guid ProjectId { get; set; }
        public string WorkItemQuery { get; set; }
    }

    class Program
    {
        static readonly string FILE_CACHE = "tfsquery_" + DateTime.Today.ToString("yyyyMMdd") + ".json";
        static readonly string VSS_OPTIONS_FILE = @"tfsclientoptions.json";

        static IEnumerable<string> fields = new List<string> { "System.Id", "System.WorkItemType", "System.TeamProject", "System.Title", "System.AssignedTo", "System.State", "System.Tags", "Microsoft.VSTS.Common.ActivatedDate", "Microsoft.VSTS.Common.BacklogPriority", "Microsoft.VSTS.Common.ClosedDate", "Microsoft.VSTS.CodeReview.ClosedStatus", "System.CreatedDate", "Microsoft.VSTS.Scheduling.FinishDate", "Microsoft.VSTS.Scheduling.Effort", "Microsoft.VSTS.Common.Priority", "Microsoft.VSTS.Common.Severity", "Microsoft.VSTS.Scheduling.StartDate" };

        static VssConnectionDetails VssOptions = new VssConnectionDetails()
        {
            CollectionUrl = @"<<url goes here>>",
            ProjectName = @"ProjectNameHere",
            PersonalAccessToken = "<<Create PAT from USER account>>",
            ProjectId = Guid.Empty,
            EditItemUriFormat = @"https://sometfs.com/tfs/DefaultCollection/SomeProject/_workitems/edit/{0}",
            WorkItemQuery = @"
SELECT 
    [System.Id],
    [System.WorkItemType],
    [System.Title],
    [System.AssignedTo],
    [System.State],
    [System.Tags] 
FROM WorkItems 
WHERE 
    [System.TeamProject] = @project 
    AND [System.WorkItemType] <> '' 
    AND [System.State] <> '' 
    AND [Microsoft.VSTS.Common.ClosedDate] > @today - 3 
    AND [Microsoft.VSTS.Common.ClosedDate] < @today + 1"
        };

        static void Main(string[] args)
        {
            if (!File.Exists(VSS_OPTIONS_FILE))
            {
                Console.WriteLine($"{VSS_OPTIONS_FILE} required to connect, dummy file has been created.");
                File.WriteAllText(VSS_OPTIONS_FILE, JsonConvert.SerializeObject(VssOptions));
                Console.WriteLine("press any key to exit");
                Console.ReadKey();
                return;
            }
            else
            {
                var json = File.ReadAllText(VSS_OPTIONS_FILE);
                VssOptions = JsonConvert.DeserializeObject<VssConnectionDetails>(json);
            }

            var store = new List<ExtendedWorkItem>();
            var serializer = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };

            if (File.Exists(FILE_CACHE))
            {
                store = JsonConvert.DeserializeObject<List<ExtendedWorkItem>>(File.ReadAllText(FILE_CACHE));
            }
            else
            {
                var creds = new VssBasicCredential(string.Empty, VssOptions.PersonalAccessToken);
                var witClient = CreateClient(VssOptions.CollectionUrl, creds);
                store = GetTfsItems(VssOptions.ProjectName, witClient, VssOptions.WorkItemQuery);
                File.WriteAllText(FILE_CACHE, JsonConvert.SerializeObject(store));
            }

            foreach (var item in store)
            {
                var r = item.Relations.Where(t => t.Rel == "System.LinkTypes.Hierarchy-Reverse").Select(rel => GetWorkItemId(rel.Url));
                item.Parents = store.Where(s => r.Any(id => s.Id == id)).ToList();
                var f = item.Relations.Where(t => t.Rel == "System.LinkTypes.Hierarchy-Forward").Select(rel => GetWorkItemId(rel.Url));
                item.Children = store.Where(s => f.Any(id => s.Id == id)).ToList();
            }

            store = store.OrderBy(s => s.ClosedDate).ToList();

            var parents = store.Where(e => e.Children.Any());
            foreach (var item in parents.Take(10))
            {
                Console.WriteLine($"{item.Title} - {item.CreatedDate} - {item.ClosedDate}");
                foreach (var child in item.Children)
                {
                    Console.WriteLine($"\t{child.Title} - {child.AssignedTo}");
                }
            }

            var types = store.Where(e => !string.IsNullOrWhiteSpace(e.ItemType)).GroupBy(e => e.ItemType).ToList();

            var outputFile = "output.html";

            if (File.Exists(outputFile)) { File.Delete(outputFile); }
            using (var tw = File.CreateText(outputFile))
            {
                WriteHtmlForGroupedItemsByAsignee(tw, store);
            }


            var now = DateTime.Now;
        }

        private static void WriteHtmlForGroupedItemsByAsignee(StreamWriter tw, List<ExtendedWorkItem> store)
        {
            var assigned = store.Where(e => !string.IsNullOrWhiteSpace(e.AssignedTo)).GroupBy(e => e.AssignedTo).ToList();

            foreach (var perUser in assigned)
            {
                var tasks = perUser.Where(e => e.ItemType == "Task").SelectMany(t => t.Parents);
                var allparents = perUser.Where(e => e.ItemType != "Task").Union(tasks).OrderBy(t => t.ClosedDate);
                tw.WriteLine($"<h1>User {perUser.Key}</h1>\r\n<ul>");
                foreach (var parent in allparents)
                {
                    tw.WriteLine($"\t<li><a href='{string.Format(VssOptions.EditItemUriFormat, parent.Id)}'>{parent.Title}</a> {parent.ClosedDate}");
                    tw.WriteLine($"<ul>");
                    foreach (var child in parent.Children)
                    {
                        tw.WriteLine($"\t\t<li>{child.Title} {child.AssignedTo}</li>\r\n");
                    }
                    tw.WriteLine("</ul></li>");
                }
                tw.WriteLine("</ul>");
            }


        }

        private static List<ExtendedWorkItem> GetTfsItems(string projectName, WorkItemTrackingHttpClient witClient, string qItems)
        {
            var serializer = new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore };
            List<ExtendedWorkItem> store = new List<ExtendedWorkItem>();
            Task.WaitAll(Task.Run(async () =>
            {
                var result = await witClient.QueryByWiqlAsync(new Wiql { Query = qItems }, projectName);

                await GetItemsInBatches(witClient, result, batch =>
                {
                    var json = JsonConvert.SerializeObject(batch, serializer);
                    var ext = JsonConvert.DeserializeObject<List<ExtendedWorkItem>>(json);
                    store.AddRange(ext);
                });
                var results = store.Count;
            }));
            return store;
        }

        public static int GetWorkItemId(string uri)
        {
            /// valid: https://project.maxcode.nl/tfs/DefaultCollection/_apis/wit/workItems/70163
            var result = -1;
            if (int.TryParse(uri.Substring(uri.LastIndexOf('/') + 1), out result))
            {
                return result;
            };
            return result;
        }

        public static WorkItem GetParent(IEnumerable<WorkItem> cache, WorkItem item)
        {
            var all = cache.Where(cached => cached
            .Relations.Any(rel =>
                    (
                        rel.Rel == "System.LinkTypes.Hierarchy-Forward"
                    //||
                    //rel.Rel == "System.LinkTypes.Hierarchy-Reverse"
                    )
                    &&
                    GetWorkItemId(rel.Url) == item.Id
                )
            ).ToList();

            return all.FirstOrDefault();
        }

        public static IEnumerable<WorkItem> GetChildren(IEnumerable<WorkItem> cache, WorkItem item)
        {
            var childIds = item.Relations.Where(rel => rel.Rel == "System.LinkTypes.Hierarchy-Reverse" || rel.Rel == "System.LinkTypes.Hierarchy-Forward").Select(r => GetWorkItemId(r.Url)).ToList();
            var children = cache.Where(t => childIds.Any(c => c == t.Id)).ToList();
            return children;
        }

        private static async Task GetItemsInBatches(WorkItemTrackingHttpClient witClient, WorkItemQueryResult result, Action<List<WorkItem>> processBatch)
        {
            if (!result.WorkItems.Any())
            {
                return;
            }
            int skip = 0;
            const int batchSize = 100;
            IEnumerable<WorkItemReference> workItemRefs;
            do
            {
                workItemRefs = result.WorkItems.Skip(skip).Take(batchSize);
                if (workItemRefs.Any())
                {
                    // get details for each work item in the batch
                    var workItems = await witClient.GetWorkItemsAsync(workItemRefs.Select(wir => wir.Id), null, null, WorkItemExpand.All);

                    processBatch(workItems);
                }
                skip += batchSize;
            }
            while (workItemRefs.Count() == batchSize);
        }

        private static WorkItemTrackingHttpClient CreateClient(string collectionUri, VssCredentials credentials)
        {
            VssConnection connection = new VssConnection(new Uri(collectionUri), credentials);

            WorkItemTrackingHttpClient witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            return witClient;
        }
    }


}
