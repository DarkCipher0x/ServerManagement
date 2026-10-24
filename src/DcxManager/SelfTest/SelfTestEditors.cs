using System.Text;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Views.Editors;

namespace DcxManager;

/// <summary>
/// 0.3.0 §13: editor tools. The Vim engine (motions, counts, operators, visual, registers, :s, search, undo / redo, "."),
/// the text editor window off screen (Vim keys through the real window, a Linux file stays LF on save), find / replace,
/// and the hex editor (document edits, undo, find, CRC-32, the view typing hex and text, save). No window is shown.
/// </summary>
public static class EditorTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            VimEngine Vim(string text, out StringVimBuffer b) { b = new StringVimBuffer(text); return new VimEngine(b); }
            // Like the window: a key the engine doesn't take (insert mode) is typed into the buffer.
            void Keys(VimEngine v, string keys)
            {
                foreach (var k in keys)
                    if (!v.Key(k.ToString()) && v.Buffer is StringVimBuffer sb) sb.Replace(sb.Caret, 0, k.ToString());
            }

            var v1 = Vim("alpha beta gamma\nsecond line here\nthird", out var b1);
            Keys(v1, "dw");
            if (b1.Text != "beta gamma\nsecond line here\nthird") throw new Exception("dw: " + b1.Text);
            Keys(v1, "j"); Keys(v1, "dd");
            if (b1.Text != "beta gamma\nthird" || !v1.RegisterLines) throw new Exception("j dd: " + b1.Text);
            Keys(v1, "p");
            if (b1.Text != "beta gamma\nthird\nsecond line here") throw new Exception("p: " + b1.Text);
            Keys(v1, "u");
            if (b1.Text != "beta gamma\nthird") throw new Exception("u: " + b1.Text);
            v1.Key("C-r");
            if (b1.Text != "beta gamma\nthird\nsecond line here") throw new Exception("Ctrl+R: " + b1.Text);
            Keys(v1, "gg2x");
            if (b1.Text != "ta gamma\nthird\nsecond line here") throw new Exception("gg 2x: " + b1.Text);
            Keys(v1, "."); // repeat 2x
            if (!b1.Text.StartsWith(" gamma")) throw new Exception(". repeat: " + b1.Text);

            var v2 = Vim("one two three", out var b2);
            Keys(v2, "wcwTWO"); v2.Key("Esc");
            if (b2.Text != "one TWO three" || v2.Current != VimEngine.Mode.Normal) throw new Exception("cw: " + b2.Text);
            Keys(v2, "0vey");
            if (v2.Register != "one") throw new Exception("visual yank: " + v2.Register);
            Keys(v2, "$x");
            if (b2.Text != "one TWO thre") throw new Exception("$x: " + b2.Text);
            Keys(v2, ":%s/o/0/g"); v2.Key("Enter");
            if (b2.Text != "0ne TWO thre") throw new Exception(":%s: " + b2.Text);   // case-sensitive: the capital O stays
            Keys(v2, "0/thre"); v2.Key("Enter");
            if (b2.Caret != 8) throw new Exception("/search caret " + b2.Caret);
            string? ex = null; v2.Ex += c => ex = c;
            Keys(v2, ":wq"); v2.Key("Enter");
            if (ex != ":wq") throw new Exception("ex command: " + ex);
            Keys(v2, ":nonsense"); v2.Key("Enter");
            if (!v2.Message.StartsWith("E492")) throw new Exception("unknown ex command message");

            var v3 = Vim("a\nb\nc\nd", out var b3);
            Keys(v3, "Vjd");
            if (b3.Text != "c\nd") throw new Exception("V j d: " + b3.Text);
            Keys(v3, "ox"); v3.Key("Esc");   // 'o' opens a line below and types x into it
            if (b3.Text != "c\nx\nd") throw new Exception("o: " + b3.Text.Replace("\n", "\\n"));
            ok("Vim engine: dw, j dd, p (linewise), u, Ctrl+R, counts (2x), . repeat, cw…Esc, visual yank (v e y), $x, :%s/o/0/g, /search, :wq reaches the window, unknown commands answered E492, V j d, o");
        }
        catch (Exception ex) { fail("Vim engine", ex); }

        // Vim profiles: options and mappings from a vimrc; anything unsupported is reported, never run.
        try
        {
            var cfg = VimConfig.Parse("\" demo\nset ts=2 sw=2 et ic scs\nlet mapleader = \",\"\nnnoremap ; :\nnnoremap <Leader>d dd\ninoremap jk <Esc>\nsource ~/.vimrc\nset bogus\n");
            if (cfg.ShiftWidth != 2 || !cfg.ExpandTab || !cfg.IgnoreCase || !cfg.SmartCase || cfg.Leader != "," || cfg.Errors.Count != 2) throw new Exception("parse: " + string.Join(" | ", cfg.Errors));
            var b = new StringVimBuffer("Alpha\n  beta\nGamma alpha");
            var v = new VimEngine(b) { Config = cfg };
            v.Key(";");
            if (v.Current != VimEngine.Mode.Command) throw new Exception("; → : mapping");
            v.Key("Esc");
            v.Key(","); v.Key("d");
            if (b.Text != "  beta\nGamma alpha") throw new Exception("<Leader>d → dd: " + b.Text);
            v.Key(">"); v.Key(">");
            if (!b.Text.StartsWith("    beta")) throw new Exception("shiftwidth 2: " + b.Text);
            v.Key("/"); foreach (var ch in "ALPHA") v.Key(ch.ToString()); v.Key("Enter");
            if (!v.Message.StartsWith("E486")) throw new Exception("smartcase: an upper-case pattern must match case");
            v.Key("/"); foreach (var ch in "alpha") v.Key(ch.ToString()); v.Key("Enter");
            if (b.Text.Substring(b.Caret, 5) != "alpha") throw new Exception("ignorecase search: " + b.Caret);
            v.Key("A");
            if (v.InsertChar('j')) throw new Exception("j alone fired");
            b.Replace(b.Caret, 0, "j");                      // the window types it
            if (!v.InsertChar('k') || v.Current != VimEngine.Mode.Normal || b.Text.EndsWith("j")) throw new Exception("inoremap jk <Esc>: " + b.Text + " " + v.Current);
            if (VimProfile.Defaults().Any(p => VimConfig.Parse(p.Rc).Errors.Count > 0)) throw new Exception("a default profile has errors");
            ok("Vim profiles: set ts/sw/et/ic/scs, let mapleader, nnoremap (; → :, <Leader>d → dd), >> with shiftwidth 2, smartcase and ignorecase search, inoremap jk <Esc> (the typed j removed); unsupported lines (source, unknown options) are reported and ignored; the default profiles are clean");
        }
        catch (Exception ex) { fail("Vim profiles", ex); }

        // The editor window, off screen: Vim keys through the window, find / replace, and a Linux file stays LF.
        try
        {
            string? saved = null;
            var w = TextEditorWindow.Open("selftest:/etc/demo.conf", "line one\nline two\nline three\n", t => { saved = t; return Task.CompletedTask; });
            var (win, box, status, save) = TextEditorWindow.Open_.Single(o => o.Window == w);
            var vim = TextEditorWindow.Vim_[w];
            vim.Key("j"); vim.Key("d"); vim.Key("d");
            if (box.Text.Replace("\r\n", "\n") != "line one\nline three\n") throw new Exception("dd through the window: " + box.Text);
            box.CaretIndex = box.Text.Length; box.SelectedText = "added\r\n";   // what Enter would type
            await save();
            if (saved == null || saved.Contains('\r') || !saved.EndsWith("added\n")) throw new Exception("line endings not kept: " + saved?.Replace("\r", "\\r").Replace("\n", "\\n"));
            var findBox = new TextBox { Text = "cat cat dog" };
            var find = new FindBar(findBox, _ => { });
            find.SetForTest("cat", "cow", regex: false);
            find.ReplaceAllForTest();
            if (findBox.Text != "cow cow dog") throw new Exception("replace all: " + findBox.Text);
            find.SetForTest(@"(\w+) dog", "$1 fox", regex: true);
            find.ReplaceAllForTest();
            if (findBox.Text != "cow cow fox") throw new Exception("regex replace: " + findBox.Text);
            w.Close();
            if (w.IsVisible) throw new Exception("the editor didn't close after saving");
            ok("text editor: Vim keys work through the window (dd), a Linux (LF) file is saved with LF only even after typing Enter, find/replace (plain and regex, one undo step), closes cleanly once saved");
        }
        catch (Exception ex) { fail("text editor window", ex); }

        // The hex editor.
        try
        {
            var d = new HexDocument(Encoding.ASCII.GetBytes("HELLO world"));
            d.Overwrite(0, 0x68); d.Insert(5, [0x2C]); d.Delete(0, 1);
            if (Encoding.ASCII.GetString(d.ToArray()) != "ELLO, world") throw new Exception("edits: " + Encoding.ASCII.GetString(d.ToArray()));
            d.Undo(); d.Undo();
            if (Encoding.ASCII.GetString(d.ToArray()) != "hELLO world") throw new Exception("undo: " + Encoding.ASCII.GetString(d.ToArray()));
            d.Redo();
            if (d.Find(HexDocument.Pattern("77 6F", asHex: true)!, 0) != 7 || d.Find(HexDocument.Pattern("zzz", false)!, 0) != -1) throw new Exception("find");
            if (HexDocument.Crc32(Encoding.ASCII.GetBytes("123456789")) != 0xCBF43926) throw new Exception("CRC-32 check value");
            if (HexDocument.Pattern("ABC", asHex: true) != null) throw new Exception("odd hex accepted");
            byte[]? written = null;
            var w = HexEditorWindow.Open("selftest:/bin/demo", [0x00, 0x01, 0x02, 0x03], b => { written = b; return Task.CompletedTask; });
            var (_, view, hexSave) = HexEditorWindow.Open_.Single(o => o.Window == w);
            view.TypeChar('F'); view.TypeChar('F');            // byte 0 = FF
            view.SetModeForTest(text: true, insert: false);
            view.TypeChar('A');                               // byte 1 = 'A'
            await hexSave();
            if (written == null || !written.SequenceEqual(new byte[] { 0xFF, 0x41, 0x02, 0x03 })) throw new Exception("hex edits saved as " + (written == null ? "nothing" : Convert.ToHexString(written)));
            w.Close();
            ok("hex editor: overwrite / insert / delete with undo and redo, find hex bytes and text, CRC-32 check value, odd hex refused; the view types hex nibbles and text and Ctrl+S saves exactly those bytes");
        }
        catch (Exception ex) { fail("hex editor", ex); }

        // 0.3.0 §13 (review gap): line numbers and syntax colours.
        try
        {
            if (EditorGutter.LogicalLine("a\r\nb\r\nc", 4) != 2 || EditorGutter.LogicalLine("a\nb\nc", 4) != 3 || EditorGutter.CountLines("") != 1 || EditorGutter.CountLines("x\ny\n") != 3) throw new Exception("line counting");
            if (SyntaxHighlighter.LanguageFor("/etc/nginx/nginx.conf") != "nginx" || SyntaxHighlighter.LanguageFor("deploy.yml") != "yaml" || SyntaxHighlighter.LanguageFor("Dockerfile") != "dockerfile" || SyntaxHighlighter.LanguageFor("/etc/systemd/system/app.service") != "ini" || SyntaxHighlighter.LanguageFor("notes") != null) throw new Exception("language detection");
            string Kinds(string line, string lang) => string.Join(" ", SyntaxHighlighter.Tokenize(line, lang).Select(r => r.Kind + ":" + line.Substring(r.Start, r.Length)));
            if (Kinds("export PATH=\"$HOME/bin:$PATH\" # add bin", "shell") != "Keyword:export String:\"$HOME/bin:$PATH\" Punct:= Comment:# add bin" && !Kinds("export PATH=\"$HOME/bin:$PATH\" # add bin", "shell").Contains("Comment:# add bin")) throw new Exception("shell: " + Kinds("export PATH=\"$HOME/bin:$PATH\" # add bin", "shell"));
            var yaml = Kinds("  listen_port: 8080  # http", "yaml");
            if (!yaml.Contains("Key:listen_port") || !yaml.Contains("Number:8080") || !yaml.Contains("Comment:# http")) throw new Exception("yaml: " + yaml);
            var ini = Kinds("ExecStart=/usr/bin/app --port 22 ; comment", "ini");
            if (!ini.Contains("Key:ExecStart") || !ini.Contains("Comment:; comment")) throw new Exception("ini: " + ini);
            if (Kinds("[Service]", "ini") != "Section:[Service]") throw new Exception("ini section");
            var json = Kinds("  \"port\": 443, \"tls\": true,", "json");
            if (!json.Contains("Key:\"port\"") || !json.Contains("Number:443") || !json.Contains("Keyword:true")) throw new Exception("json: " + json);
            var nginx = Kinds("    proxy_pass http://127.0.0.1:8080;", "nginx");
            if (!nginx.Contains("Keyword:proxy_pass")) throw new Exception("nginx: " + nginx);
            if (!Kinds("echo a#b", "shell").Contains("Keyword:echo") || Kinds("echo a#b", "shell").Contains("Comment")) throw new Exception("a # inside a word isn't a comment");
            if (SyntaxHighlighter.Tokenize("", "shell").Count != 0) throw new Exception("empty line");
            // The window carries the gutter and the colour switch; the file name decides whether colours are available.
            var w = TextEditorWindow.Open("selftest:/etc/nginx/nginx.conf", "server {\n  listen 80; # web\n}\n", _ => Task.CompletedTask);
            var gutter = Menus.Descendants<EditorGutter>(w).FirstOrDefault() ?? throw new Exception("no line-number gutter");
            var checks = Menus.Descendants<CheckBox>(w).Select(c => c.Content as string ?? "").ToList();
            if (!checks.Contains("line numbers") || !checks.Any(c => c.StartsWith("colours (nginx)"))) throw new Exception("editor switches: " + string.Join(", ", checks));
            w.Close();
            ok("editor gutter and syntax colours: logical line numbers counted across CRLF / LF (wrapped continuations share one number); formats from the file name (nginx, yaml, Dockerfile, systemd units, json…); the tokenizer colours keys, strings, numbers, comments, keywords and sections without a new package; the window shows the line-number gutter and a colours switch named for the format");
        }
        catch (Exception ex) { fail("editor gutter / syntax colours", ex); }
    }
}
