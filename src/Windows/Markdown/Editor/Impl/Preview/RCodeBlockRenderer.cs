﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Common.Core;
using Microsoft.Common.Core.Disposables;
using Microsoft.Common.Core.Services;
using Microsoft.R.Components.InteractiveWorkflow;
using Microsoft.R.Components.Settings;
using Microsoft.R.Host.Client;
using mshtml;
using static System.FormattableString;

namespace Microsoft.Markdown.Editor.Preview {
    /// <summary>
    /// Renders R code block output into HTML element
    /// </summary>
    internal sealed class RCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>, IDisposable {
        private static string _placeholderImage;

        /// <summary>
        /// Rendered results cache. Caches HTML for every code block.
        /// Blocks are differentiated by their order of appearance
        /// and the contents hash.
        /// </summary>
        private readonly List<RCodeBlock> _blocks = new List<RCodeBlock>();

        private readonly IServiceContainer _services;
        private readonly string _sessionId;
        private readonly CancellationTokenSource _hostStartCts = new CancellationTokenSource();
        private readonly RSessionCallback _sessionCallback = new RSessionCallback();

        private CancellationTokenSource _blockEvalCts = new CancellationTokenSource();
        private IRSession _session;
        private int _blockNumber;
        private Task _evalTask;

        public RCodeBlockRenderer(string documentName, IServiceContainer services) {
            _services = services;
            _sessionId = Invariant($"({documentName} - {Guid.NewGuid()}");
            StartSessionAsync(_hostStartCts.Token).DoNotWait();
        }

        public IDisposable StartRendering() {
            _blockNumber = 0;
            _blockEvalCts?.Cancel();
            _blockEvalCts = new CancellationTokenSource();
            return Disposable.Create(() => _evalTask = EvaluateBlocksAsync(_blockEvalCts.Token));
        }

        public async Task RenderBlocks(HTMLDocument htmlDocument) {
            await _evalTask;
            var blocks = _blocks.ToArray();
            foreach (var b in blocks.Where(b => b.State == CodeBlockEvalState.Evaluated)) {
                var element = htmlDocument.getElementById(b.HtmlElementId);
                if (element != null) {
                    element.innerHTML = b.Result;
                    b.State = CodeBlockEvalState.Rendered;
                }
            }
        }

        protected override void Write(HtmlRenderer renderer, CodeBlock codeBlock) {
            renderer.EnsureLine();

            var fencedCodeBlock = codeBlock as FencedCodeBlock;
            if (fencedCodeBlock?.Info != null && fencedCodeBlock.Info.StartsWithIgnoreCase("{r")) {
                var text = GetBlockText(fencedCodeBlock);
                var hash = text.GetHashCode();

                var result = GetCachedResult(_blockNumber, hash, fencedCodeBlock);
                if (result != null) {
                    WriteBlockContent(renderer, _blockNumber, text);
                    renderer.Write(result);
                } else {
                    var rCodeBlock = new RCodeBlock(_blockNumber, fencedCodeBlock.Arguments, text, hash);
                    var elementId = rCodeBlock.HtmlElementId;
                    _blocks.Add(rCodeBlock);

                    WriteBlockContent(renderer, _blockNumber, text);
                    // Write placeholder first. We will insert actual data when the evaluation is done.
                    renderer.Write(GetBlockPlaceholder(elementId));
                }
                _blockNumber++;
            }
        }

        private void WriteBlockContent(HtmlRenderer renderer, int blockNumber, string text) {
            if (_blocks[blockNumber].EchoContent) {
                renderer.Write(Invariant($"<pre class='r'><code>{text}</code></pre>"));
            }
        }

        private string GetCachedResult(int blockNumber, int hash, FencedCodeBlock block) {
            if (blockNumber >= _blocks.Count) {
                return null;
            }
            if (_blocks[_blockNumber].Hash != hash) {
                InvalidateCacheFrom(_blockNumber);
                return null;
            }
            // can be null if block hasn't been rendered yet
            return _blocks[_blockNumber].Result;
        }

        private void InvalidateCacheFrom(int index) {
            if (index < _blocks.Count) {
                _blocks.RemoveRange(index, _blocks.Count - index);
            }
        }

        private string GetBlockPlaceholder(string elementId) {
            var base64Image = GetPlaceholderImage();
            return Invariant($"<div id='{elementId}'><img src='data:image/gif;base64, {base64Image}' width='32' height='32' /></div>");
        }

        private static string GetPlaceholderImage() {
            if (_placeholderImage == null) {
                using (var ms = new MemoryStream()) {
                    Resources.Loading.Save(ms, ImageFormat.Gif);
                    _placeholderImage = Convert.ToBase64String(ms.ToArray());
                }
            }
            return _placeholderImage;
        }

        private static string GetBlockText(FencedCodeBlock block) {
            var sb = new StringBuilder();
            foreach (var line in block.Lines.Lines) {
                sb.AppendLine(line.ToString());
            }
            return sb.ToString().Trim();
        }

        private Task EvaluateBlocksAsync(CancellationToken ct) {
            var blocks = _blocks.ToArray();
            //TODO: clear session on cache drop
            return Task.Run(async () => {
                try {
                    var session = await StartSessionAsync(ct);

                    foreach (var block in blocks.Where(b => b.State == CodeBlockEvalState.Created)) {
                        await block.EvaluateAsync(session, _sessionCallback, ct);
                    }
                } catch (OperationCanceledException) { }
            }, ct);
        }

        private async Task<IRSession> StartSessionAsync(CancellationToken ct) {
            if (_session == null) {
                var workflow = _services.GetService<IRInteractiveWorkflowProvider>().GetOrCreate();
                _session = workflow.RSessions.GetOrCreate(_sessionId);
            }
            if (!_session.IsHostRunning) {
                var settings = _services.GetService<IRSettings>();
                await _session.EnsureHostStartedAsync(
                    new RHostStartupInfo(settings.CranMirror, codePage: settings.RCodePage), _sessionCallback, 3000, ct);
            }

            return _session;
        }

        public void Dispose() {
            _blockEvalCts.Cancel();
            _hostStartCts.Cancel();
            _session?.Dispose();
        }
    }
}
