﻿using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using System.Reflection;
using Microsoft.VisualStudio.TextTemplating;
using Microsoft.VisualStudio.LanguageServices;
using System.Data;
using Xamarin.HotReload.Vsix;
using System.Threading.Tasks;
using Reloadify;
using Microsoft.VisualStudio.ComponentModelHost;

namespace CometReloadVisix
{

    internal class Constants
    {
        public const string MonoTouchUnifiedProjectGuidString = "FEACFBD2-3405-455C-9665-78FE426C6842";
        public const string MonodroidProjectGuidString = "EFBA0AD7-5A72-4C68-AF49-83D382785DCF";
        public const string WpfProjectGuidsString = "60DC8134-EBA5-43B8-BCC9-BB4BC16C2548";

        public const string LoadContextRuleGuidString = "B9F16EE3-1431-4F5F-84ED-8778D01423E4";
    }

    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the
    /// IVsPackage interface and uses the registration attributes defined in the framework to
    /// register itself and its components with the shell. These attributes tell the pkgdef creation
    /// utility what data to put into .pkgdef file.
    /// </para>
    /// <para>
    /// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
    /// </para>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(CometReloadVisixPackage.PackageGuidString)]

    [ProvideBindingPath]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasMultipleProjects_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionHasSingleProject_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]

    public sealed class CometReloadVisixPackage : AsyncPackage, IVsDebuggerEvents
    {
        /// <summary>
        /// CometReloadVisixPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "BFF3BF4B-914F-4453-9426-72F1D6B16173";

        public static CometReloadVisixPackage Instance { get; private set; }

        IVsSolution vsSolution;
        IVsDebugger debuggerService;
        IVsRunningDocumentTable runningDocTable;
        IVsOutputWindowPane debugOutputPane;
        IVsOutputWindowPane generalOutputPane;
        IVsStatusbar statusBar;
        SolutionEvents solutionEvents;
        VisualStudioWorkspace workspace;

        DocumentEvents documentEvents;
        TextEditorEvents textEditorEvents;

        uint debugEventsCookie = 0;
        uint docTableCookie = 0;
        DBGMODE debugMode = DBGMODE.DBGMODE_Design;
        bool isDebugging = false;


        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Console.WriteLine("Initialized Reloadify3000");
            Instance = this;
            IDEManager.Shared.GetActiveDocumentText = GetCodeFromActiveDocument;
            await base.InitializeAsync(cancellationToken, progress);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);


            var dte = (DTE)(await GetServiceAsync(typeof(DTE)));
            vsSolution = (IVsSolution)Package.GetGlobalService(typeof(IVsSolution));

            InitializeGeneralOutputPane();



            //ide.AgentStatusChanged += IdeManager_AgentStatusChanged;
            //ide.AgentViewAppeared += IdeManager_AgentViewAppeared;
            //ide.AgentViewDisappeared += IdeManager_AgentViewDisappeared;
            //ide.AgentReloadResultReceived += IdeManager_AgentXamlResultReceived;

            //ide.Logger.Log(Info, "Hot Reload IDE Extension Loaded");

            // NOTE: GetServiceAsync for IVsDebugger failed with "No Such Interface Supported",
            //       which is why we still use the synchronous call that requires main thread access. -sandy
            debuggerService = (IVsDebugger)GetService(typeof(IVsDebugger));
            debuggerService.AdviseDebuggerEvents(this, out debugEventsCookie);

            var componentModel = (IComponentModel)this.GetService(typeof(SComponentModel));
            workspace = componentModel.GetService<VisualStudioWorkspace>();

            statusBar = (IVsStatusbar)GetService(typeof(SVsStatusbar));

            solutionEvents = dte.Events.SolutionEvents;
            solutionEvents.Opened += OnSolutionOpened;
            solutionEvents.AfterClosing += OnAfterSolutionClosing;
            documentEvents = dte.Events.DocumentEvents;
            documentEvents.DocumentSaved += DocumentEvents_DocumentSaved;
            textEditorEvents = dte.Events.TextEditorEvents;
            textEditorEvents.LineChanged += TextEditorEvents_LineChanged;
        }

        async Task<string> GetCodeFromActiveDocument(string filePath)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE)(await GetServiceAsync(typeof(DTE)));
            var file = dte.ActiveDocument.FullName;
            if (file != filePath)
                return null;
            var textDoc = dte.ActiveDocument.Object("TextDocument") as TextDocument;
            var edit = textDoc.StartPoint.CreateEditPoint();
            var text = edit.GetText(textDoc.EndPoint);
            return text;
        }

        private async void TextEditorEvents_LineChanged(TextPoint StartPoint, TextPoint EndPoint, int Hint)
        {
            if (!isDebugging || !IDEManager.Shared.IsEnabled || !shouldRun)
                return;
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = (DTE)(await GetServiceAsync(typeof(DTE)));
            var file = dte.ActiveDocument?.FullName;
            if (string.IsNullOrWhiteSpace(file))
                return;
            IDEManager.Shared.TextChanged(file);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) 
            {
                //ide.AgentStatusChanged -= IdeManager_AgentStatusChanged;
                //ide.AgentViewAppeared -= IdeManager_AgentViewAppeared;
                //ide.AgentViewDisappeared -= IdeManager_AgentViewDisappeared;
                //ide.AgentReloadResultReceived -= IdeManager_AgentXamlResultReceived;
                //ide = null;

                debuggerService?.UnadviseDebuggerEvents(debugEventsCookie);
                if(documentEvents != null)
                {
                    documentEvents.DocumentSaved -= DocumentEvents_DocumentSaved;
                }
                if(textEditorEvents != null)
                {
                    textEditorEvents.LineChanged -= TextEditorEvents_LineChanged;
                }
                if (solutionEvents != null)
                {
                    solutionEvents.Opened -= OnSolutionOpened;
                    solutionEvents.AfterClosing -= OnAfterSolutionClosing;

                }

                Cleanup();

                System.Diagnostics.Trace.Flush();
            }

            base.Dispose(disposing);
        }

        void OnSolutionOpened() {} // => RefreshHotReloadBridge ();
        void OnAfterSolutionClosing() => Cleanup();

        void Cleanup()
        {
            InitializeGeneralOutputPane();

            IDEManager.Shared?.StopMonitoring();


            if (docTableCookie > 0 && runningDocTable != null)
            {
                runningDocTable.UnadviseRunningDocTableEvents(docTableCookie);
                runningDocTable = null;
                docTableCookie = 0;
            }
        }
        bool shouldRun;
        public int OnModeChange(DBGMODE dbgmodeNew)
        {
            var lastDebugMode = debugMode;
            debugMode = dbgmodeNew;

            if (lastDebugMode == DBGMODE.DBGMODE_Break && dbgmodeNew == DBGMODE.DBGMODE_Run)
                return VSConstants.S_OK;

            // Design means the debugger is stopped. The other modes are runtime modes.
            if (dbgmodeNew == DBGMODE.DBGMODE_Design)
            {
                isDebugging = false;
                Cleanup();
                return VSConstants.S_OK;
            }

            if (dbgmodeNew == DBGMODE.DBGMODE_Break)
                return VSConstants.S_OK;

            Task.Run(async () =>
            {
                if (IDEManager.Shared.IsEnabled)
                {
                    IDEManager.Shared.CurrentProjectPath = GetStartupProject().FileName;
                    IDEManager.Shared.Solution = workspace.CurrentSolution;
                    var project = workspace.CurrentSolution.Projects.FirstOrDefault(x => string.Equals(x.FilePath, IDEManager.Shared.CurrentProjectPath, StringComparison.OrdinalIgnoreCase));

                    InitializeDebugOutputPane();
                    shouldRun = await RoslynCodeManager.Shared.ShouldHotReload(project);
                    if (shouldRun)
                        IDEManager.Shared.StartMonitoring();
                   
                }
            });
            isDebugging = true;
            return VSConstants.S_OK;
        }


        Project GetStartupProject()
        {
            try
            {
                var dte = GetService(typeof(DTE)) as DTE;

                var startupProjNames = (Array)dte.Solution.SolutionBuild.StartupProjects;
                if (startupProjNames == null || startupProjNames.Length < 1)
                    return null;

                var startupProjName = (string)startupProjNames.GetValue(0);

                return dte
                    .Solution
                    .GetProjects()
                    .FirstOrDefault(x => x.UniqueName == startupProjName);
            }
            catch (Exception e)
            {
                //ide?.Logger?.Log(e);
                return null;
            }
        }


        static string GetAssemblyPath(EnvDTE.Project vsProject)
        {

            string fullPath = vsProject.Properties.Item("FullPath").Value.ToString();

            string outputPath = vsProject.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString();

            string outputDir = Path.Combine(fullPath, outputPath);

            string outputFileName = vsProject.Properties.Item("OutputFileName").Value.ToString();

            string assemblyPath = Path.Combine(outputDir, outputFileName);

            return assemblyPath;

        }


        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;

        void TestOutPRojects(DTE dte)
		{
            //Microsoft.CodeAnalysis.Solution solution = CreateUnitTestBoilerplateCommandPackage.VisualStudioWorkspace.CurrentSolution;
            //DocumentId documentId = solution.GetDocumentIdsWithFilePath(inputFilePath).FirstOrDefault();
            //var document = solution.GetDocument(documentId);

            //SyntaxNode root = await document.GetSyntaxRootAsync();
            //SemanticModel semanticModel = await document.GetSemanticModelAsync();

            //var componentModel = (IComponentModel)this.GetService(typeof(SComponentModel));
            //VisualStudioWorkspace = componentModel.GetService<VisualStudioWorkspace>();

            var projects = dte.ActiveSolutionProjects as Project[];
            if(projects != null)
			{
                foreach(var p in projects)
				{
                    var items = p.ProjectItems;
                    foreach(var i in items)
					{
                        Console.WriteLine(i);
					}
				}
			}
		}


        private async void DocumentEvents_DocumentSaved(Document Document)
        {

            if (!isDebugging || !IDEManager.Shared.IsEnabled || !shouldRun)
                return;
            try
            {
                var docPath = Document.FullName;
                if (!string.IsNullOrEmpty(docPath))
                {
                    var fileInfo = new FileInfo(docPath);

                    if (fileInfo.Exists && fileInfo.Extension.TrimStart('.').Equals("cs", StringComparison.OrdinalIgnoreCase))
                    {
                        var dte = GetService(typeof(DTE)) as DTE;

                        var doc = dte.Documents
                               .OfType<Document>()
                               .FirstOrDefault(x => x != null && x.FullName == docPath);

                        //var docEncoding = VSHelpers.GetEncoding(docPath);

                        AssemblyName assemblyName = default;

                        var assemblyNameProjectProperty = doc?.ProjectItem?.ContainingProject?.Properties?.Item("AssemblyName");

                        if (assemblyNameProjectProperty != null && assemblyNameProjectProperty.Value != null)
                            assemblyName = new AssemblyName(assemblyNameProjectProperty?.Value.ToString());

                        var rootPath = doc.ProjectItem.ContainingProject.AsMsBuildProject().DirectoryPath;
                        

                        if (!string.IsNullOrEmpty(rootPath))
                        {
                            var fileRelPath = docPath;
                            if (fileRelPath.StartsWith(rootPath))
                                fileRelPath = fileRelPath.Substring(rootPath.Length).TrimStart('\\', '/');

                            //// These agents expect / as path separator
                            //if (debuggingFlavor == ProjectFlavor.Android || debuggingFlavor == ProjectFlavor.iOS || debuggingFlavor == ProjectFlavor.Mac)
                            //{
                            //    ide?.Logger.Log(Debug, "Project is Unix based, converting relative path slashes...");
                            //    fileRelPath = fileRelPath.Replace('\\', '/');
                            //}

                            var textDoc = (TextDocument)doc.Object("TextDocument");
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            var xaml = editPoint.GetText(textDoc.EndPoint);
                            IDEManager.Shared.Solution = workspace.CurrentSolution;
                            IDEManager.Shared.HandleDocumentChanged(new DocumentChangedEventArgs(docPath, xaml));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //ide?.Logger.Log(ex);
            }
        }
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int OnBeforeSave(uint docCookie)
        {
            UpdateStatusBar("Reloading XAML...", true);

            return VSConstants.S_OK;
        }

        void InitializeDebugOutputPane()
        {
            if (debugOutputPane != null)
                return;

            debugOutputPane = InitializeOutputPane(VSConstants.GUID_OutWindowDebugPane);
        }

        void InitializeGeneralOutputPane()
        {
            if (generalOutputPane != null)
                return;

            generalOutputPane = InitializeOutputPane(VSConstants.GUID_OutWindowGeneralPane);
        }

        IVsOutputWindowPane InitializeOutputPane(Guid paneGuid)
        {
            var outWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            IVsOutputWindowPane pane;
            outWindow.GetPane(ref paneGuid, out pane);
            return pane;
        }


        async Task ShowOutputPane()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);

            if (isDebugging)
                debugOutputPane.Activate(); // Brings this pane into view
            else
                generalOutputPane.Activate();
        }

        async void UpdateStatusBar(string text, bool? animating = default)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            // Make sure the status bar is not frozen
            int frozen;

            statusBar.IsFrozen(out frozen);

            if (frozen != 0)
                statusBar.FreezeOutput(0);

            // Set the status bar text and make its display static.
            statusBar.SetText(text);

            // This was causing the text to not update not sure yet the right way to do this
            // so disabling for now
            //if (animating.HasValue)
            //{
            //    // Use the standard Visual Studio icon for building.
            //    object icon = (short)Microsoft.VisualStudio.Shell.Interop.Constants.SBAI_Build;

            //    if (animating.Value)
            //    {
            //        // Display the icon in the Animation region.
            //        statusBar.Animation(1, ref icon);
            //    }
            //    else
            //    {
            //        // Stop the animation.
            //        statusBar.Animation(0, ref icon);
            //    }
            //}


            // Freeze the status bar.
            statusBar.FreezeOutput(1);
        }
    }
}