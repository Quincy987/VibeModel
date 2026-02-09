// Based on Markdig.Wpf (MIT License) - https://github.com/Kryptos-FR/markdig.wpf
// Copyright (c) Nicolas Musset. All rights reserved.

using Markdig.Renderers;
using Markdig.Syntax;

namespace VibeModel.Markdown
{
    /// <summary>
    /// Base class for WPF rendering of Markdown objects.
    /// </summary>
    public abstract class WpfObjectRenderer<TObject> : MarkdownObjectRenderer<WpfRenderer, TObject>
        where TObject : MarkdownObject
    {
    }
}
