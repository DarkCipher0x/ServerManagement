namespace DcxManager.Services;

/// <summary>A named Vim profile for the text editor (0.3.0 §13): a small vimrc of options and key mappings (parsed by the
/// editor's VimConfig; it can only change keys and a few options — never run anything). Kept in the encrypted settings.</summary>
public sealed class VimProfile
{
    public string Name { get; set; } = "Default";
    public string Rc { get; set; } = "";
    public override string ToString() => Name;

    /// <summary>The profiles a new install starts with.</summary>
    public static List<VimProfile> Defaults() => new()
    {
        new() { Name = "Default", Rc = "\" Options and mappings for the editor's Vim mode (see Help → Editors).\nset tabstop=4 shiftwidth=4 expandtab\nset ignorecase smartcase\n" },
        new() { Name = "Servers (2-space YAML, jk = Esc)", Rc = "set tabstop=2 shiftwidth=2 expandtab\nset ignorecase smartcase\ninoremap jk <Esc>\nlet mapleader = \",\"\nnnoremap <Leader>w :w<CR>\nnnoremap <Leader>q :q<CR>\n" },
        new() { Name = "Tabs (Makefiles, Go)", Rc = "set tabstop=8 shiftwidth=8 noexpandtab\n" },
    };
}
