using System.IO;
using System.Reflection;

namespace MWCFsmProfiler
{
    // report.html: a single file with no external dependencies, so it opens offline and can be sent to anyone.
    // The template is an embedded resource; the report's JSON goes inside a <script type="application/json">
    // (JsonWriter escapes '<', so the data cannot close the tag).
    internal static class HtmlReport
    {
        private static string template;

        public static string Build(string json)
        {
            if (template == null)
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MWCFsmProfiler.report_template.html"))
                using (StreamReader r = new StreamReader(s))
                    template = r.ReadToEnd();
            }
            return template.Replace("/*DATA*/", json);
        }
    }
}
