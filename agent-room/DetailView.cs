using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentRoom
{
    internal enum DetailStyle { Body, Heading, Author, Meta, Reply, Warning }

    // Build formatting from semantic fields, never by interpreting message text as RTF.
    internal sealed class DetailDocument
    {
        private sealed class Paragraph
        {
            internal string Text;
            internal DetailStyle Style;
            internal int Level;
        }
        private readonly List<Paragraph> paragraphs = new List<Paragraph>();
        internal void Add(string text, DetailStyle style = DetailStyle.Body, int level = 0)
        { paragraphs.Add(new Paragraph { Text = text, Style = style, Level = Math.Max(0, Math.Min(4, level)) }); }
        internal string Rtf(Font font)
        {
            var text = new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 ");
            Escape(text, font.Name); text.Append(@";}}{\colortbl;\red29\green43\blue62;\red96\green108\blue124;\red36\green94\blue163;\red159\green83\blue17;\red237\green245\blue255;}\viewkind4 ");
            foreach (var paragraph in paragraphs)
            {
                bool small = paragraph.Style == DetailStyle.Meta || paragraph.Style == DetailStyle.Reply || paragraph.Style == DetailStyle.Warning;
                bool bold = paragraph.Style == DetailStyle.Heading || paragraph.Style == DetailStyle.Author;
                int color = paragraph.Style == DetailStyle.Meta ? 2 : paragraph.Style == DetailStyle.Reply ? 3 : paragraph.Style == DetailStyle.Warning ? 4 : 1;
                int size = (int)Math.Round((font.SizeInPoints + (paragraph.Style == DetailStyle.Heading ? 1 : small ? -1 : 0)) * 2);
                text.Append(@"\pard\plain\f0\fs").Append(size).Append(@"\cf").Append(color)
                    .Append(@"\li").Append(paragraph.Level * 360).Append(@"\ri120\sb")
                    .Append(paragraph.Style == DetailStyle.Author || paragraph.Style == DetailStyle.Heading ? 180 : 0)
                    .Append(@"\sa").Append(paragraph.Style == DetailStyle.Body ? 180 : 70);
                if (bold) text.Append(@"\b");
                if (paragraph.Style == DetailStyle.Reply) text.Append(@"\highlight5");
                text.Append(' '); Escape(text, paragraph.Text); text.Append(@"\par ");
            }
            return text.Append('}').ToString();
        }
        private static void Escape(StringBuilder output, string value)
        {
            foreach (char c in value.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                if (c == '\\' || c == '{' || c == '}') output.Append('\\').Append(c);
                else if (c == '\n') output.Append(@"\line ");
                else if (c == '\t') output.Append(@"\tab ");
                else if (c < 128) output.Append(c);
                else output.Append(@"\u").Append(unchecked((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            }
        }
    }

    internal sealed class DetailView : RichTextBox
    {
        private string shownRtf = "", shownKey = "";
        private DetailDocument document;
        internal DetailView()
        {
            ReadOnly = true; BorderStyle = BorderStyle.None; BackColor = Color.White; Dock = DockStyle.Fill;
            DetectUrls = false; HideSelection = false; ScrollBars = RichTextBoxScrollBars.Vertical; WordWrap = true;
            AccessibleName = "Conversation and task details";
        }
        internal void ShowText(string text)
        {
            var plain = new DetailDocument(); plain.Add(text); Render(plain, "text:" + text);
        }
        internal void Render(DetailDocument value, string key)
        {
            string rtf = value.Rtf(Font);
            document = value;
            if (shownRtf == rtf && shownKey == key) return;
            bool keepPosition = shownKey == key && IsHandleCreated;
            int start = keepPosition ? SelectionStart : 0, length = keepPosition ? SelectionLength : 0;
            var position = new Point();
            if (keepPosition) SendMessage(Handle, 0x4DD, IntPtr.Zero, ref position); // EM_GETSCROLLPOS
            bool redraw = IsHandleCreated;
            if (redraw) SendMessage(Handle, 0xB, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
            try
            {
                Rtf = rtf; shownRtf = rtf; shownKey = key;
                Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start)));
                SendMessage(Handle, 0x4DE, IntPtr.Zero, ref position); // EM_SETSCROLLPOS
            }
            finally { if (redraw) { SendMessage(Handle, 0xB, new IntPtr(1), IntPtr.Zero); Invalidate(); } }
        }
        protected override void OnFontChanged(EventArgs e)
        { base.OnFontChanged(e); if (document != null) Render(document, shownKey); }
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, ref Point point);
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr value);
    }
}
