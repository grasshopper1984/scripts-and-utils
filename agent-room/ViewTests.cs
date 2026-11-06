using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace AgentRoom
{
    internal static partial class Tests
    {
        private static void ViewTests()
        {
            var nodes = new List<Dictionary<string, object>> {
                Json.Obj("id", 1, "updated_at", 500, "priority", 0),
                Json.Obj("id", 2, "updated_at", 300, "priority", 0),
                Json.Obj("id", 3, "updated_at", 100, "activity_at", 400, "priority", 0),
                Json.Obj("id", 4, "updated_at", 200, "priority", 2),
                Json.Obj("id", 5, "updated_at", 50, "priority", 2)
            };
            var edges = new List<Dictionary<string, object>> {
                Json.Obj("source", 1, "target", 2, "kind", "dependency"),
                Json.Obj("source", 4, "target", 3, "kind", "reference")
            };
            var layout = GraphLayout.Create(nodes, edges, .8f, 600);
            var order = layout.Bounds.OrderBy(p => p.Value.Y).ThenBy(p => p.Value.X).Select(p => p.Key).ToArray();
            Check(order.SequenceEqual(new long[] { 3, 2, 4, 1, 5 }), "Recent activity chooses the first row while dependency and context branches stay aligned with their successors");
            nodes.Reverse(); edges.Reverse();
            var scaled = GraphLayout.Create(nodes, edges, 1.5f, 900);
            Check(scaled.Bounds.OrderBy(p => p.Value.Y).ThenBy(p => p.Value.X).Select(p => p.Key).SequenceEqual(order), "History order is stable across input ordering and DPI/zoom changes");
            Check(layout.Bounds[3].Top == layout.Bounds[2].Top && layout.Bounds[3].Right < layout.Bounds[2].Left && layout.Bounds[3].Width == (int)(232 * .8f),
                "Independent tasks share a row while retaining the compact card size");
            using (var f = new Fixture("graph-activity"))
            {
                long recent = Convert.ToInt64(f.Ticket("Discussion in progress")["id"]);
                f.Now += 10;
                long important = Convert.ToInt64(Result(f.Call("coordinator", "task.create", Json.Obj("title", "Older critical task", "objective", "Fixture", "acceptance", "Fixture", "priority", 2)))["id"]);
                f.Now += 10;
                Result(f.Call("alice", "post", Json.Obj("body", "Recent progress update", "task_id", recent)));
                var graph = Result(f.Call("observer", "graph", Json.Obj("limit", 1)));
                Check(Convert.ToInt64(DataRows(graph["nodes"])[0]["id"]) == recent &&
                    Convert.ToInt64(DataRows(graph["nodes"])[0]["activity_at"]) == f.Now,
                    "Published thread activity brings a task into the capped graph ahead of older high-priority work");
                Result(f.Call("moderator", "moderate", Json.Obj("action", "mute", "target", "alice", "reason", "Visibility fixture")));
                f.Now += 10;
                Result(f.Call("alice", "post", Json.Obj("body", "Held update", "task_id", important)));
                graph = Result(f.Call("observer", "graph", Json.Obj("limit", 1)));
                Check(Convert.ToInt64(DataRows(graph["nodes"])[0]["id"]) == recent, "Held chatter does not affect public graph activity ordering");
            }
            string body = "Literal {\\rtf1 \\b text} stays intact.\n中文 and emoji \U0001F680\nSecond paragraph.";
            var messages = new List<Dictionary<string, object>> {
                ViewMessage(1, null, "First request"), ViewMessage(2, null, "Independent message"),
                ViewMessage(3, 1, body), ViewMessage(4, 99, "Reply whose original is on an earlier page")
            };
            var thread = Json.Obj("task", null, "messages", messages, "older", true);
            using (var view = new DetailView())
            {
                view.Font = new Font("Segoe UI", 10);
                view.Render(Viewer.ThreadDocument(thread), "fixture");
                Check(view.Text.Contains(body), "Styled conversation preserves literal RTF syntax, Unicode, emoji, and multiline message bodies");
                Check(view.Text.IndexOf("#3  @alice", StringComparison.Ordinal) < view.Text.IndexOf("#2  @alice", StringComparison.Ordinal) &&
                    view.Text.Contains("Reply to #1 · @alice: First request"), "Reply is grouped beneath its parent with an explicit parent preview");
                Check(view.Text.Contains("Reply to #99 · Original message is not on this page") && view.Text.Contains("Reply whose original"),
                    "Paged conversations retain replies whose parent is absent and explain the missing original");
                int selected = view.Text.IndexOf("Literal", StringComparison.Ordinal);
                view.Select(selected, 7); view.Render(Viewer.ThreadDocument(thread), "fixture");
                Check(view.SelectionStart == selected && view.SelectedText == "Literal", "Unchanged transcript refresh preserves the reader's selection");
                messages.Add(ViewMessage(5, null, "New activity"));
                view.Render(Viewer.ThreadDocument(thread), "fixture");
                Check(view.SelectionStart == selected && view.SelectedText == "Literal", "Appended thread activity preserves the current selection");
            }
        }
        private static Dictionary<string, object> ViewMessage(long id, object reply, string body)
        {
            return Json.Obj("id", id, "author", "alice", "recipients", "[\"coordinator\"]", "kind", "note", "status", "", "at", 1800000000,
                "reply_to", reply, "body", body, "open_ask", 0, "visibility", "published", "held_reason", "");
        }
    }
}
