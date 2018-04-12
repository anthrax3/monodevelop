//
// MonoDevelopDocumentTrackingService.cs
//
// Author:
//       Marius <>
//
// Copyright (c) 2018 ${CopyrightHolder}
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;
using Microsoft.VisualStudio.Text;

namespace MonoDevelop.Ide.TypeSystem
{
	[ExportWorkspaceServiceFactory (typeof (IDocumentTrackingService), ServiceLayer.Host), Shared]
	sealed class MonoDevelopDocumentTrackingService : IWorkspaceServiceFactory
	{
		IDocumentTrackingService service;

		public IWorkspaceService CreateService (HostWorkspaceServices workspaceServices)
		{
			return service ?? (service = new MonoDevelopDocumentTrackingService ());
		}
	}

	[DebuggerDisplay ("{GetDebuggerDisplay(),nq}")]
	class MonoDevelopDocumentTrackingService : IDocumentTrackingService, IVsSelectionEvents, IDisposable
	{
		IVsMonitorSelection _monitorSelection;
		uint _cookie;
		ImmutableList<FrameListener> _visibleFrames;
		IVsWindowFrame _activeFrame;

		public MonoDevelopDocumentTrackingService ()
		{
			_visibleFrames = ImmutableList<FrameListener>.Empty;

			_monitorSelection = (IVsMonitorSelection)serviceProvider.GetService (typeof (SVsShellMonitorSelection));
			_monitorSelection.AdviseSelectionEvents (this, out _cookie);
		}

		public event EventHandler<DocumentId> ActiveDocumentChanged;

		/// <summary>
		/// Get the <see cref="DocumentId"/> of the active document. May be called from any thread.
		/// May return null if there is no active document or the active document is not part of this
		/// workspace.
		/// </summary>
		/// <returns>The ID of the active document (if any)</returns>
		public DocumentId GetActiveDocument ()
		{
			MonoDevelop.ide
			var snapshot = _visibleFrames;
			if (_activeFrame == null || snapshot.IsEmpty) {
				return null;
			}

			foreach (var listener in snapshot) {
				if (listener.Frame == _activeFrame) {
					return listener.Id;
				}
			}

			return null;
		}

		/// <summary>
		/// Get a read only collection of the <see cref="DocumentId"/>s of all the visible documents in the workspace.
		/// </summary>
		public ImmutableArray<DocumentId> GetVisibleDocuments ()
		{
			var snapshot = _visibleFrames;
			if (snapshot.IsEmpty) {
				return ImmutableArray.Create<DocumentId> ();
			}

			var ids = ArrayBuilder<DocumentId>.GetInstance (snapshot.Count);
			foreach (var frame in snapshot) {
				ids.Add (frame.Id);
			}

			return ids.ToImmutableAndFree ();
		}

		/// <summary>
		/// Called via the DocumentProvider's RDT OnBeforeDocumentWindowShow notification when a workspace document is being shown.
		/// </summary>
		/// <param name="frame">The frame containing the document being shown.</param>
		/// <param name="id">The <see cref="DocumentId"/> of the document being shown.</param>
		/// <param name="firstShow">Indicates whether this is a first or subsequent show.</param>
		public void DocumentFrameShowing (IVsWindowFrame frame, DocumentId id, bool firstShow)
		{
			Contract.ThrowIfNull (frame);
			Contract.ThrowIfNull (id);

			if (!firstShow && !_visibleFrames.IsEmpty) {
				foreach (FrameListener frameListener in _visibleFrames) {
					if (frameListener.Frame == frame) {
						// Already in the visible list
						return;
					}
				}
			}

			_visibleFrames = _visibleFrames.Add (new FrameListener (this, frame, id));
		}

		private void RemoveFrame (FrameListener frame)
		{
			_visibleFrames = _visibleFrames.Remove (frame);
		}

		public int OnSelectionChanged (IVsHierarchy pHierOld, [ComAliasName ("Microsoft.VisualStudio.Shell.Interop.VSITEMID")]uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld, IVsHierarchy pHierNew, [ComAliasName ("Microsoft.VisualStudio.Shell.Interop.VSITEMID")]uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
		{
			return VSConstants.E_NOTIMPL;
		}

		public int OnElementValueChanged ([ComAliasName ("Microsoft.VisualStudio.Shell.Interop.VSSELELEMID")]uint elementid, object varValueOld, object varValueNew)
		{
			if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame) {
				// Remember the newly activated frame so it can be read from another thread.
				_activeFrame = varValueNew as IVsWindowFrame;
				this.ActiveDocumentChanged?.Invoke (this, GetActiveDocument ());
			}

			return VSConstants.S_OK;
		}

		public int OnCmdUIContextChanged ([ComAliasName ("Microsoft.VisualStudio.Shell.Interop.VSCOOKIE")]uint dwCmdUICookie, [ComAliasName ("Microsoft.VisualStudio.OLE.Interop.BOOL")]int fActive)
		{
			return VSConstants.E_NOTIMPL;
		}

		public event EventHandler<EventArgs> NonRoslynBufferTextChanged;

		public void OnNonRoslynBufferOpened (ITextBuffer buffer)
		{
			buffer.PostChanged += OnNonRoslynBufferChanged;
		}

		public void OnNonRoslynBufferClosed (ITextBuffer buffer)
		{
			buffer.PostChanged -= OnNonRoslynBufferChanged;
		}

		private void OnNonRoslynBufferChanged (object sender, EventArgs e)
		{
			this.NonRoslynBufferTextChanged?.Invoke (sender, e);
		}
		public void Dispose ()
		{
			if (_cookie != VSConstants.VSCOOKIE_NIL && _monitorSelection != null) {
				_activeFrame = null;
				_monitorSelection.UnadviseSelectionEvents (_cookie);
				_monitorSelection = null;
				_cookie = VSConstants.VSCOOKIE_NIL;
			}

			var snapshot = _visibleFrames;
			_visibleFrames = ImmutableList<FrameListener>.Empty;

			if (!snapshot.IsEmpty) {
				foreach (var frame in snapshot) {
					frame.Dispose ();
				}
			}
		}

		private string GetDebuggerDisplay ()
		{
			var snapshot = _visibleFrames;

			StringBuilder sb = new StringBuilder ();
			sb.Append ("Visible frames: ");
			if (snapshot.IsEmpty) {
				sb.Append ("{empty}");
			} else {
				foreach (var frame in snapshot) {
					sb.Append (frame.GetDebuggerDisplay ());
					sb.Append (' ');
				}
			}

			return sb.ToString ();
		}

		/// <summary>
		/// Listens to frame notifications for a visible frame. When the frame becomes invisible or closes,
		/// then it automatically disconnects.
		/// </summary>
		[DebuggerDisplay ("{GetDebuggerDisplay(),nq}")]
		private class FrameListener : IVsWindowFrameNotify, IVsWindowFrameNotify2, IDisposable
		{
			public readonly IVsWindowFrame Frame;
			public readonly DocumentId Id;

			private readonly VisualStudioDocumentTrackingService _service;
			private uint _cookie;

			public FrameListener (VisualStudioDocumentTrackingService service, IVsWindowFrame frame, DocumentId id)
			{
				this.Frame = frame;
				this.Id = id;
				_service = service;

				((IVsWindowFrame2)frame).Advise (this, out _cookie);
			}

			public int OnDockableChange (int fDockable)
			{
				return VSConstants.S_OK;
			}

			public int OnMove ()
			{
				return VSConstants.S_OK;
			}

			public int OnShow (int fShow)
			{
				switch ((__FRAMESHOW)fShow) {
				case __FRAMESHOW.FRAMESHOW_WinClosed:
				case __FRAMESHOW.FRAMESHOW_WinHidden:
				case __FRAMESHOW.FRAMESHOW_TabDeactivated:
					return Disconnect ();
				}

				return VSConstants.S_OK;
			}

			public int OnSize ()
			{
				return VSConstants.S_OK;
			}

			public int OnClose (ref uint pgrfSaveOptions)
			{
				return Disconnect ();
			}

			private int Disconnect ()
			{
				_service.RemoveFrame (this);
				return Unadvise ();
			}

			private int Unadvise ()
			{
				int hr = VSConstants.S_OK;

				if (_cookie != VSConstants.VSCOOKIE_NIL) {
					hr = ((IVsWindowFrame2)Frame).Unadvise (_cookie);
					_cookie = VSConstants.VSCOOKIE_NIL;
				}

				return hr;
			}

			public void Dispose ()
			{
				Unadvise ();
			}

			internal string GetDebuggerDisplay ()
			{
				Frame.GetProperty ((int)__VSFPROPID.VSFPROPID_Caption, out var caption);
				return caption.ToString ();
			}
		}
	}
}