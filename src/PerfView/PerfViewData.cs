using Diagnostics.Tracing.StackSources;
using EventSources;
using Graphs;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AspNet;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.ClrPrivate;
using Microsoft.Diagnostics.Tracing.Parsers.ETWClrProfiler;
using Microsoft.Diagnostics.Tracing.Parsers.JSDumpHeap;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Parsers.Tpl;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Utilities;
using global::DiagnosticsHub.Packaging.Interop;
using Microsoft.DiagnosticsHub.Packaging.InteropEx;
using PerfView.GuiUtilities;
using PerfViewModel;
using Stats;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using Triggers;
using Utilities;
using Address = System.UInt64;
using EventSource = EventSources.EventSource;

namespace PerfView
{
    /// <summary>
    /// PerfViewTreeItem is a common base class for something that can be represented in the 
    /// TreeView GUI.  in particular it has a name, and children.   This includes both
    /// file directories as well as data file (that might have multiple sources inside them)
    /// </summary>
    public abstract class PerfViewTreeItem : INotifyPropertyChanged
    {
        /// <summary>
        /// The name to place in the treeview (should be short).  
        /// </summary>
        public string Name { get; protected set; }
        /// <summary>
        /// If the entry should have children in the TreeView, this is them.
        /// </summary>
        public virtual IList<PerfViewTreeItem> Children { get { return m_Children; } }
        /// <summary>
        /// All items have some sort of file path that is associated with them.  
        /// </summary>
        public virtual string FilePath { get { return m_filePath; } }
        public virtual string HelpAnchor { get { return this.GetType().Name; } }

        public bool IsExpanded { get { return m_isExpanded; } set { m_isExpanded = value; FirePropertyChanged("IsExpanded"); } }
        public bool IsSelected { get { return m_isSelected; } set { m_isSelected = value; FirePropertyChanged("IsSelected"); } }

        /// <summary>
        /// Open the file (This might be expensive (but maybe not).  It should not be run on
        /// the GUI thread.  This should populate the Children property if that is appropriate.  
        /// 
        /// if 'doAfter' is present, it will be run after the window has been opened.   It is always
        /// executed on the GUI thread.  
        /// </summary>
        public abstract void Open(Window parentWindow, StatusBar worker, Action doAfter = null);
        /// <summary>
        /// Once something is opened, it should be closed.  
        /// </summary>
        public abstract void Close();

        // Support so that we can update children and the view will update
        public event PropertyChangedEventHandler PropertyChanged;
        protected void FirePropertyChanged(string propertyName)
        {
            var propertyChanged = PropertyChanged;
            if (propertyChanged != null)
                propertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// The Icon to show next to the entry.  
        /// </summary>
        public virtual ImageSource Icon { get { return GuiApp.MainWindow.Resources["StackSourceBitmapImage"] as ImageSource; } }
        #region private
        public override string ToString() { if (FilePath != null) return FilePath; return Name; }

        protected List<PerfViewTreeItem> m_Children;
        protected List<PerfViewReport> m_UserDeclaredChildren;
        protected string m_filePath;

        bool m_isExpanded;
        bool m_isSelected;
        #endregion
    }

    public class PerfViewDirectory : PerfViewTreeItem
    {
        // Only names that match this filter are displayed. 
        public Regex Filter
        {
            get { return m_filter; }
            set
            {
                if (m_filter != value)
                {
                    m_filter = value;
                    m_Children = null;
                    FirePropertyChanged("Children");
                }
            }
        }
        public PerfViewDirectory(string path)
        {
            m_filePath = path;
            Name = System.IO.Path.GetFileName(path);
        }

        public override IList<PerfViewTreeItem> Children
        {
            get
            {
                if (m_Children == null)
                {
                    m_Children = new List<PerfViewTreeItem>();
                    if (Name != "..")
                    {
                        try
                        {
                            foreach (var filePath in FilesInDirectory(m_filePath))
                            {
                                var template = PerfViewFile.TryGet(filePath);
                                if (template != null)
                                {
                                    // Filter out kernel, rundown files etc. 
                                    if (Regex.IsMatch(filePath, @"\.(kernel|clr|user)[^.]*\.etl$", RegexOptions.IgnoreCase))
                                        continue;

                                    // Filter out any items we were asked to filter out.  
                                    if (m_filter != null && !m_filter.IsMatch(Path.GetFileName(filePath)))
                                        continue;

                                    m_Children.Add(PerfViewFile.Get(filePath, template));
                                }
                            }

                            foreach (var dir in DirsInDirectory(m_filePath))
                            {
                                // Filter out any items we were asked to filter out.  
                                if (m_filter != null && !m_filter.IsMatch(Path.GetFileName(dir)))
                                    continue;
                                // We know that .NGENPDB directories are uninteresting, filter them out.  
                                if (dir.EndsWith(".NENPDB", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                m_Children.Add(new PerfViewDirectory(dir));
                            }

                            // We always have the parent directory.  
                            m_Children.Add(new PerfViewDirectory(System.IO.Path.Combine(m_filePath, "..")));
                        }
                        // FIX NOW review
                        catch (Exception) { }
                    }
                }
                return m_Children;
            }
        }
        public override string HelpAnchor { get { return null; } }      // Don't bother with help for this.  

        /// <summary>
        /// Open the file (This might be expensive (but maybe not).  This should populate the Children property 
        /// too.  
        /// </summary>
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter)
        {
            var mainWindow = parentWindow as MainWindow;
            if (mainWindow != null)
                mainWindow.OpenPath(FilePath);

            if (doAfter != null)
                doAfter();
        }
        /// <summary>
        /// Close the file
        /// </summary>
        public override void Close() { }

        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FolderOpenBitmapImage"] as ImageSource; } }

        #region private

        class DirCacheEntry
        {
            public string[] FilesInDirectory;
            public string[] DirsInDirectory;
            public DateTime LastWriteTimeUtc;
        }
        // To speed things up we remember the list list of directory items we fetched from disk
        static Dictionary<string, DirCacheEntry> s_dirCache = new Dictionary<string, DirCacheEntry>();

        static string[] FilesInDirectory(string directoryPath)
        {
            var entry = GetDirEntry(directoryPath);
            if (entry.FilesInDirectory == null)
                entry.FilesInDirectory = Directory.GetFiles(directoryPath);
            return entry.FilesInDirectory;
        }
        static string[] DirsInDirectory(string directoryPath)
        {
            var entry = GetDirEntry(directoryPath);
            if (entry.DirsInDirectory == null)
                entry.DirsInDirectory = Directory.GetDirectories(directoryPath);
            return entry.DirsInDirectory;
        }

        /// <summary>
        /// Gets a cache entry, nulls it out if it is out of date.  
        /// </summary>
        static DirCacheEntry GetDirEntry(string directoryPath)
        {
            DateTime lastWrite = Directory.GetLastWriteTimeUtc(directoryPath);
            DirCacheEntry entry;
            if (!s_dirCache.TryGetValue(directoryPath, out entry))
                s_dirCache[directoryPath] = entry = new DirCacheEntry();

            if (lastWrite != entry.LastWriteTimeUtc)
            {
                entry.DirsInDirectory = null;
                entry.FilesInDirectory = null;
            }
            entry.LastWriteTimeUtc = lastWrite;
            return entry;
        }

        private Regex m_filter;
        #endregion
    }

    /// <summary>
    /// A PerfViewTreeGroup simply groups other Items.  Thus it has a name, and you use the Children
    /// to add Child nodes to the group.  
    /// </summary>
    public class PerfViewTreeGroup : PerfViewTreeItem
    {
        public PerfViewTreeGroup(string name)
        {
            Name = name;
            m_Children = new List<PerfViewTreeItem>();
        }

        public PerfViewTreeGroup AddChild(PerfViewTreeItem child)
        {
            m_Children.Add(child);
            return this;
        }

        // Groups do no semantic action.   All the work is in the visual GUI part.  
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            if (doAfter != null)
                doAfter();
        }
        public override void Close() { }

        public override IList<PerfViewTreeItem> Children { get { return m_Children; } }

        public override string HelpAnchor { get { return Name.Replace(" ", ""); } }

        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FolderOpenBitmapImage"] as ImageSource; } }
    }

    /// <summary>
    /// PerfViewData is an abstraction of something that PerfViewGui knows how to display.   It is 
    /// </summary>
    public abstract class PerfViewFile : PerfViewTreeItem
    {
        public bool IsOpened { get { return m_opened; } }
        public bool IsUpToDate { get { return m_utcLastWriteAtOpen == File.GetLastWriteTimeUtc(FilePath); } }

        /// <summary>
        /// Get does not actually open the file (which might be expensive).   It also does not
        /// populate the children for the node (which again might be expensive).  Instead it 
        /// just looks at the file name).   It DOES however determine how this data will be
        /// treated from here on (based on file extension or an explicitly passed template parameter.
        /// 
        /// Get implements interning, so if you Get the same full file path, then you will get the
        /// same PerfViewDataFile structure. 
        /// 
        /// After you have gotten a PerfViewData, you use instance methods to manipulate it
        /// 
        /// This routine throws if the path name does not have a suffix we understand.  
        /// </summary>
        public static PerfViewFile Get(string filePath, PerfViewFile format = null)
        {
            var ret = TryGet(filePath, format);
            if (ret == null)
                throw new ApplicationException("Could not determine data Template from the file extension for " + filePath + ".");
            return ret;
        }
        /// <summary>
        /// Tries to to a 'Get' operation on filePath.   If format == null (indicating
        /// that we should try to determine the type of the file from the file suffix) and 
        /// it does not have a suffix we understand, then we return null.   
        /// </summary>
        public static PerfViewFile TryGet(string filePath, PerfViewFile format = null)
        {
            if (format == null)
            {
                // See if it is any format we recognize.  
                foreach (PerfViewFile potentalFormat in Formats)
                {
                    if (potentalFormat.IsMyFormat(filePath))
                    {
                        format = potentalFormat;
                        break;
                    };
                }
                if (format == null)
                    return null;
            }

            string fullPath = Path.GetFullPath(filePath);
            PerfViewFile ret;
            if (!s_internTable.TryGetValue(fullPath, out ret))
            {
                ret = (PerfViewFile)format.MemberwiseClone();
                ret.m_filePath = filePath;
                s_internTable[fullPath] = ret;
            }
            ret.Name = Path.GetFileName(ret.FilePath);
            if (ret.Name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(ret.FilePath);
                if (dir.Length == 0)
                    dir = ".";

                var wildCard = ret.Name.Insert(ret.Name.Length - 4, ".*");
                if (Directory.GetFiles(dir, wildCard).Length > 0)
                    ret.Name += " (unmerged)";
            }
            return ret;
        }

        /// <summary>
        /// Logs the fact that the GUI should call a user defined method when a file is opened.  
        /// </summary>
        public static PerfViewFile GetTemplateForExtension(string extension)
        {
            foreach (PerfViewFile potentalFormat in Formats)
            {
                if (potentalFormat.IsMyFormat(extension))
                    return potentalFormat;
            }
            var ret = new PerfViewUserFile(extension + " file", new string[] { extension });
            Formats.Add(ret);
            return ret;
        }

        /// <summary>
        /// Declares that the user command 'userCommand' (that takes one string argument) 
        /// should be called when the file is opened.  
        /// </summary>
        public void OnOpenFile(string userCommand)
        {
            if (userCommands == null)
                userCommands = new List<string>();
            userCommands.Add(userCommand);
        }

        /// <summary>
        /// Declares that the file should have a view called 'viewName' and the user command
        /// 'userCommand' (that takes two string arguments (file, viewName)) should be called 
        /// when that view is opened 
        /// </summary>
        public void DeclareFileView(string viewName, string userCommand)
        {
            if (m_UserDeclaredChildren == null)
                m_UserDeclaredChildren = new List<PerfViewReport>();

            m_UserDeclaredChildren.Add(new PerfViewReport(viewName, delegate (string reportFileName, string reportViewName)
            {
                PerfViewExtensibility.Extensions.ExecuteUserCommand(userCommand, new string[] { reportFileName, reportViewName });
            }));
        }

        internal void ExecuteOnOpenCommand(StatusBar worker)
        {
            if (m_UserDeclaredChildren != null)
            {
                // The m_UserDeclaredChildren are templates.  We need to instantiate them to this file before adding them as children. 
                foreach (var userDeclaredChild in m_UserDeclaredChildren)
                    m_Children.Add(new PerfViewReport(userDeclaredChild, this));
                m_UserDeclaredChildren = null;
            }
            // Add the command to the list 
            if (userCommands == null)
                return;

            var args = new string[] { FilePath };
            foreach (string command in userCommands)
            {
                worker.LogWriter.WriteLine("Running User defined OnFileOpen command " + command);
                try
                {
                    PerfViewExtensibility.Extensions.ExecuteUserCommand(command, args);
                }
                catch (Exception e)
                {
                    worker.LogError(@"Error executing OnOpenFile command " + command + "\r\n" + e.ToString());
                }
            }
            return;
        }

        // A list of user commands to be executed when a file is opened 
        List<string> userCommands;

        /// <summary>
        /// Retrieves the base file name from a PerfView data source's name.
        /// </summary>
        /// <example>
        /// GetBaseName(@"C:\data\foo.bar.perfView.xml.zip") == "foo.bar"
        /// </example>
        /// <param name="filePath">The path to the data source.</param>
        /// <returns>The base name, without extensions or path, of <paramref name="filePath"/>.</returns>
        public static string GetFileNameWithoutExtension(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            foreach (var fmt in Formats)
            {
                foreach (string ext in fmt.FileExtensions)
                {
                    if (fileName.EndsWith(ext))
                    {
                        return fileName.Substring(0, fileName.Length - ext.Length);
                    }
                }
            }

            return fileName;
        }
        /// <summary>
        /// Change the extension of a PerfView data source path.
        /// </summary>
        /// <param name="filePath">The path to change.</param>
        /// <param name="newExtension">The new extension to add.</param>
        /// <returns>The path to a file with the same directory and base name of <paramref name="filePath"/>, 
        /// but with extension <paramref name="newExtension"/>.</returns>
        public static string ChangeExtension(string filePath, string newExtension)
        {
            string dirName = Path.GetDirectoryName(filePath);
            string fileName = GetFileNameWithoutExtension(filePath) + newExtension;
            return Path.Combine(dirName, fileName);
        }

        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            if (!m_opened)
            {
                worker.StartWork("Opening " + Name, delegate ()
                {
                    Action<Action> continuation = OpenImpl(parentWindow, worker);
                    ExecuteOnOpenCommand(worker);
                    worker.EndWork(delegate ()
                    {
                        m_opened = true;
                        FirePropertyChanged("Children");

                        IsExpanded = true;
                        var defaultSource = GetStackSource();
                        if (defaultSource != null)
                            defaultSource.IsSelected = true;

                        if (continuation != null)
                            continuation(doAfter);
                        else if (doAfter != null)
                            doAfter();
                    });
                });
            }
            else
            {
                if (m_singletonStackSource != null && m_singletonStackSource.Viewer != null)
                    m_singletonStackSource.Viewer.Focus();
            }
        }
        public override void Close()
        {
            if (m_opened)
            {
                m_opened = false;
                s_internTable.Remove(FilePath);
            }

            if (m_Children != null)
            {
                m_Children.Clear();
                FirePropertyChanged("Children");
            }
        }

        public virtual PerfViewStackSource GetStackSource(string sourceName = null)
        {
            if (sourceName == null)
            {
                sourceName = DefaultStackSourceName;
                if (sourceName == null)
                    return null;
            }

            Debug.Assert(m_opened);
            if (m_Children != null)
            {
                foreach (var child in m_Children)
                {
                    var asStackSource = child as PerfViewStackSource;
                    if (asStackSource != null && asStackSource.SourceName == sourceName)
                        return asStackSource;
                    var asGroup = child as PerfViewTreeGroup;
                    if (asGroup != null && asGroup.Children != null)
                    {
                        foreach (var groupChild in asGroup.Children)
                        {
                            asStackSource = groupChild as PerfViewStackSource;
                            if (asStackSource != null && asStackSource.SourceName == sourceName)
                                return asStackSource;
                        }
                    }
                }
            }
            else if (m_singletonStackSource != null)
            {
                var asStackSource = m_singletonStackSource as PerfViewStackSource;
                if (asStackSource != null)
                    return asStackSource;
            }
            return null;
        }
        public virtual string DefaultStackSourceName { get { return "CPU"; } }

        /// <summary>
        /// Gets or sets the processes to be initially included in stack views. The default value is null. 
        /// The names are not case sensitive.
        /// </summary>
        /// <remarks>
        /// If this is null, a dialog will prompt the user to choose the initially included processes
        /// from a list that contains all processes from the trace.
        /// If this is NOT null, the 'Choose Process' dialog will never show and the initially included
        /// processes will be all processes with a name in InitiallyIncludedProcesses.
        /// </remarks>
        /// <example>
        /// Let's say these are the processes in the trace: devenv (104), PerfWatson2, (67) devenv (56)
        /// and InitiallyIncludedProcesses = ["devenv", "vswinexpress"].
        /// When the user opens a stack window, the included filter will be set to "^Process32% devenv (104)|^Process32% devenv (56)"
        /// </example>
        public string[] InitiallyIncludedProcesses { get; set; }
        /// <summary>
        /// If the stack sources have their first tier being the Process, then SupportsProcesses should be true.  
        /// </summary>
        public virtual bool SupportsProcesses { get { return false; } }
        /// <summary>
        /// If the source logs data from multiple processes, this gives a list
        /// of those processes.  Returning null means you don't support this.  
        /// 
        /// This can take a while.  Don't call on the GUI thread.  
        /// </summary>
        public virtual List<IProcess> GetProcesses(TextWriter log)
        {
            // This can take a while, should not be on GUI thread.  
            Debug.Assert(GuiApp.MainWindow.Dispatcher.Thread != System.Threading.Thread.CurrentThread);

            var dataSource = GetStackSource(DefaultStackSourceName);
            if (dataSource == null)
                return null;
            StackSource stackSource = dataSource.GetStackSource(log);

            // maps call stack indexes to callStack closest to the root.
            var rootMostFrameCache = new Dictionary<StackSourceCallStackIndex, IProcessForStackSource>();
            var processes = new List<IProcess>();

            DateTime start = DateTime.Now;
            stackSource.ForEach(delegate (StackSourceSample sample)
            {
                if (sample.StackIndex != StackSourceCallStackIndex.Invalid)
                {
                    var process = GetProcessFromStack(sample.StackIndex, stackSource, rootMostFrameCache, processes);
                    if (process != null)
                    {
                        process.CPUTimeMSec += sample.Metric;
                        long sampleTicks = start.Ticks + (long)(sample.TimeRelativeMSec * 10000);
                        if (sampleTicks < process.StartTime.Ticks)
                            process.StartTime = new DateTime(sampleTicks);
                        if (sampleTicks > process.EndTime.Ticks)
                            process.EndTime = new DateTime(sampleTicks);
                        Debug.Assert(process.EndTime >= process.StartTime);
                    }
                }
            });
            processes.Sort();
            if (processes.Count == 0)
                processes = null;
            return processes;
        }

        public virtual string Title
        {
            get
            {
                // Arrange the title putting most descriptive inTemplateion first.  
                var fullName = m_filePath;
                Match m = Regex.Match(fullName, @"(([^\\]+)\\)?([^\\]+)$");
                return m.Groups[3].Value + " in " + m.Groups[2].Value + " (" + fullName + ")";
            }
        }

        public virtual void LookupSymbolsForModule(string simpleModuleName, TextWriter log, int processId = 0)
        {
            throw new ApplicationException("This file type does not support lazy symbol resolution.");
        }

        // Things a subclass should be overriding 
        /// <summary>
        /// The name of the file format.
        /// </summary>
        public abstract string FormatName { get; }
        /// <summary>
        /// The file extensions that this format knows how to read.  
        /// </summary>
        public abstract string[] FileExtensions { get; }
        /// <summary>
        /// Implements the open operation.   Executed NOT on the GUI thread.   Typically returns null
        /// which means the open is complete.  If some operation has to be done on the GUI thread afterward
        /// then  action(doAfter) continuation is returned.  This function is given an addition action 
        /// that must be done at the every end.   
        /// </summary>
        protected virtual Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            return delegate (Action doAfter)
            {
                // By default we have a singleton source (which we don't show on the GUI) and we immediately open it
                m_singletonStackSource = new PerfViewStackSource(this, "");
                m_singletonStackSource.Open(parentWindow, worker);
                if (doAfter != null)
                    doAfter();
            };
        }

        protected internal virtual void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow) { }
        /// <summary>
        /// Allows you to do a firt action after everything is done.  
        /// </summary>
        protected internal virtual void FirstAction(StackWindow stackWindow) { }
        protected internal virtual StackSource OpenStackSourceImpl(
            string streamName, TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity, Predicate<TraceEvent> predicate = null)
        {
            return null;
        }
        /// <summary>
        /// Simplified form, you should implement one overload or the other.  
        /// </summary>
        /// <returns></returns>
        protected internal virtual StackSource OpenStackSourceImpl(TextWriter log) { return null; }
        protected internal virtual EventSource OpenEventSourceImpl(TextWriter log) { return null; }

        // Helper functions for ConfigStackWindowImpl (we often configure windows the same way)
        internal static void ConfigureAsMemoryWindow(string stackSourceName, StackWindow stackWindow)
        {
            bool walkableObjectView = HeapDumpPerfViewFile.Gen0WalkableObjectsViewName.Equals(stackSourceName) || HeapDumpPerfViewFile.Gen1WalkableObjectsViewName.Equals(stackSourceName);

            // stackWindow.ScalingPolicy = ScalingPolicyKind.TimeMetric;
            stackWindow.IsMemoryWindow = true;
            stackWindow.FoldPercentTextBox.Text = stackWindow.GetDefaultFoldPercentage();
            var defaultFold = "[];mscorlib!String";
            if (!walkableObjectView)
            {
                stackWindow.FoldRegExTextBox.Text = defaultFold;
            }
            stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFold);

            var defaultExclusions = "[not reachable from roots]";
            stackWindow.ExcludeRegExTextBox.Text = defaultExclusions;
            stackWindow.ExcludeRegExTextBox.Items.Insert(0, defaultExclusions);

            stackWindow.GroupRegExTextBox.Items.Insert(0, "[group modules]           {%}!->module $1");
            stackWindow.GroupRegExTextBox.Items.Insert(0, "[group full path module entries]  {*}!=>module $1");
            stackWindow.GroupRegExTextBox.Items.Insert(0, "[group module entries]  {%}!=>module $1");

            var defaultGroup = @"[group Framework] mscorlib!=>LIB;System%!=>LIB;";
            if (!walkableObjectView)
            {
                stackWindow.GroupRegExTextBox.Text = defaultGroup;
            }
            stackWindow.GroupRegExTextBox.Items.Insert(0, defaultGroup);

            stackWindow.PriorityTextBox.Text = Graphs.MemoryGraphStackSource.DefaultPriorities;

            stackWindow.RemoveColumn("WhenColumn");
            stackWindow.RemoveColumn("WhichColumn");
            stackWindow.RemoveColumn("FirstColumn");
            stackWindow.RemoveColumn("LastColumn");
            stackWindow.RemoveColumn("IncAvgColumn");
        }
        internal static void ConfigureAsEtwStackWindow(StackWindow stackWindow, bool removeCounts = true, bool removeScenarios = true, bool removeIncAvg = true)
        {
            if (removeCounts)
            {
                stackWindow.RemoveColumn("IncCountColumn");
                stackWindow.RemoveColumn("ExcCountColumn");
                stackWindow.RemoveColumn("FoldCountColumn");
            }

            if (removeScenarios)
                stackWindow.RemoveColumn("WhichColumn");

            if (removeIncAvg)
                stackWindow.RemoveColumn("IncAvgColumn");

            var defaultEntry = stackWindow.GetDefaultFoldPat();
            stackWindow.FoldRegExTextBox.Text = defaultEntry;
            stackWindow.FoldRegExTextBox.Items.Clear();
            if (!string.IsNullOrWhiteSpace(defaultEntry))
                stackWindow.FoldRegExTextBox.Items.Add(defaultEntry);
            if (defaultEntry != "ntoskrnl!%ServiceCopyEnd")
                stackWindow.FoldRegExTextBox.Items.Add("ntoskrnl!%ServiceCopyEnd");

            stackWindow.GroupRegExTextBox.Text = stackWindow.GetDefaultGroupPat();
            stackWindow.GroupRegExTextBox.Items.Clear();
            stackWindow.GroupRegExTextBox.Items.Add(@"[no grouping]");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group CLR/OS entries] \Temporary ASP.NET Files\->;v4.0.30319\%!=>CLR;v2.0.50727\%!=>CLR;mscoree=>CLR;\mscorlib.*!=>LIB;\System.*!=>LIB;Presentation%=>WPF;WindowsBase%=>WPF;system32\*!=>OS;syswow64\*!=>OS;{%}!=> module $1");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group modules]           {%}!->module $1");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group module entries]  {%}!=>module $1");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group full path module entries]  {*}!=>module $1");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group class entries]     {%!*}.%(=>class $1;{%!*}::=>class $1");
            stackWindow.GroupRegExTextBox.Items.Add(@"[group classes]            {%!*}.%(->class $1;{%!*}::->class $1");
        }

        // ideally this function would not exist.  Does the open logic on the current thread (likely GUI thread) 
        internal void OpenWithoutWorker(Window parentWindow, StatusBar worker)
        {
            OpenImpl(parentWindow, worker);
        }

        // This is the global list of all known file types.  
        private static List<PerfViewFile> Formats = new List<PerfViewFile>()
        {
            new CSVPerfViewData(),
            new ETLPerfViewData(),
            new WTPerfViewFile(),
            new ClrProfilerCodeSizePerfViewFile(),
            new ClrProfilerAllocStacksPerfViewFile(),
            new XmlPerfViewFile(),
            new ClrProfilerHeapPerfViewFile(),
            new PdbScopePerfViewFile(),
            new VmmapPerfViewFile(),
            new DebuggerStackPerfViewFile(),
            new HeapDumpPerfViewFile(),
            new ProcessDumpPerfViewFile(),
            new ScenarioSetPerfViewFile(),
            new OffProfPerfViewFile(),
            new DiagSessionPerfViewFile(),
            new LinuxPerfViewData(),
        };

        #region private
        internal void StackSourceClosing(PerfViewStackSource dataSource)
        {
            // TODO FIX NOW.   WE need reference counting 
            if (m_singletonStackSource != null)
                m_opened = false;
        }

        protected PerfViewFile() { }        // Don't allow public default constructor
        /// <summary>
        /// Gets the process from the stack.  It assumes that the stack frame closest to the root is the process
        /// name and returns an IProcess representing it.  
        /// </summary>
        private IProcessForStackSource GetProcessFromStack(StackSourceCallStackIndex callStack, StackSource stackSource,
            Dictionary<StackSourceCallStackIndex, IProcessForStackSource> rootMostFrameCache, List<IProcess> processes)
        {
            Debug.Assert(callStack != StackSourceCallStackIndex.Invalid);

            IProcessForStackSource ret;
            if (rootMostFrameCache.TryGetValue(callStack, out ret))
                return ret;

            var caller = stackSource.GetCallerIndex(callStack);
            if (caller == StackSourceCallStackIndex.Invalid)
            {
                string topCallStackStr = stackSource.GetFrameName(stackSource.GetFrameIndex(callStack), true);
                Match m = Regex.Match(topCallStackStr, @"^Process\d*\s+([^()]*?)\s*(\(\s*(\d+)\s*\))?\s*$");
                if (m.Success)
                {
                    var processIDStr = m.Groups[3].Value;
                    var processName = m.Groups[1].Value;
                    if (processName.Length == 0)
                        processName = "(" + processIDStr + ")";
                    ret = new IProcessForStackSource(processName);
                    int processID;
                    if (int.TryParse(processIDStr, out processID))
                        ret.ProcessID = processID;
                    processes.Add(ret);
                }
            }
            else
                ret = GetProcessFromStack(caller, stackSource, rootMostFrameCache, processes);

            rootMostFrameCache.Add(callStack, ret);
            return ret;
        }

        protected bool IsMyFormat(string fileName)
        {
            foreach (var extension in FileExtensions)
                if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        protected bool m_opened;
        internal protected DateTime m_utcLastWriteAtOpen;

        // If we have only one stack source put it here
        protected PerfViewStackSource m_singletonStackSource;

        private static Dictionary<string, PerfViewFile> s_internTable = new Dictionary<string, PerfViewFile>();
        #endregion
    }

    // Used for new user defined file formats.  
    class PerfViewUserFile : PerfViewFile
    {
        public PerfViewUserFile(string formatName, string[] fileExtensions)
        {
            m_formatName = formatName;
            m_fileExtensions = fileExtensions;
        }

        public override string FormatName { get { return m_formatName; } }
        public override string[] FileExtensions { get { return m_fileExtensions; } }
        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker) { return null; }

        #region private
        string m_formatName;
        string[] m_fileExtensions;
        #endregion
    }

    public class PerfViewReport : PerfViewTreeItem
    {
        // Used to create a template for all PerfViewFiles
        public PerfViewReport(string name, Action<string, string> onOpen)
        {
            Name = name;
            m_onOpen = onOpen;
        }

        // Used to clone a PerfViewReport and specialize it to a particular data file.  
        internal PerfViewReport(PerfViewReport template, PerfViewFile dataFile)
        {
            Name = template.Name;
            m_onOpen = template.m_onOpen;
            DataFile = dataFile;
        }

        #region overrides
        public virtual string Title { get { return Name + " for " + DataFile.Title; } }
        public PerfViewFile DataFile { get; private set; }
        public override string FilePath { get { return DataFile.FilePath; } }
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter)
        {
            m_onOpen(DataFile.FilePath, Name);
        }
        public override void Close() { }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["HtmlReportBitmapImage"] as ImageSource; } }
        #endregion
        #region private
        Action<string, string> m_onOpen;
        #endregion
    }

    /// <summary>
    /// Represents a report from an ETL file that can be viewed in a web browsers.  Subclasses need 
    /// to override OpenImpl().  
    /// </summary>
    public abstract class PerfViewHtmlReport : PerfViewTreeItem
    {
        public PerfViewHtmlReport(PerfViewFile dataFile, string name)
        {
            DataFile = dataFile;
            Name = name;
        }
        public virtual string Title { get { return Name + " for " + DataFile.Title; } }
        public PerfViewFile DataFile { get; private set; }
        public WebBrowserWindow Viewer { get; internal set; }
        public override string FilePath { get { return DataFile.FilePath; } }
        protected abstract void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log);
        /// <summary>
        /// You can make Command:XXXX urls which come here when users click on them.   
        /// Returns an  error message (or null if it succeeds).  
        /// </summary>
        protected virtual string DoCommand(string command, StatusBar worker)
        {
            return "Unimplemented command: " + command;
        }

        protected virtual string DoCommand(string command, StatusBar worker, out Action continuation)
        {
            continuation = null;
            return DoCommand(command, worker);
        }

        public override void Open(Window parentWindow, StatusBar worker, Action doAfter)
        {
            if (Viewer == null)
            {
                var etlDataFile = DataFile as ETLPerfViewData;
                TraceLog trace = null;
                if (etlDataFile != null)
                {
                    trace = etlDataFile.GetTraceLog(worker.LogWriter);
                }
                else
                {
                    var linuxDataFile = DataFile as LinuxPerfViewData;
                    if (linuxDataFile != null)
                    {
                        trace = linuxDataFile.GetTraceLog(worker.LogWriter);
                    }
                }

                worker.StartWork("Opening " + Name, delegate ()
                {
                    var reportFileName = CacheFiles.FindFile(FilePath, "." + Name + ".html");
                    using (var writer = File.CreateText(reportFileName))
                    {
                        writer.WriteLine("<html>");
                        writer.WriteLine("<head>");
                        writer.WriteLine("<title>{0}</title>", Title);
                        writer.WriteLine("<meta charset=\"UTF-8\"/>");
                        writer.WriteLine("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>");
                        writer.WriteLine("</head>");
                        writer.WriteLine("<body>");
                        WriteHtmlBody(trace, writer, reportFileName, worker.LogWriter);
                        writer.WriteLine("</body>");
                        writer.WriteLine("</html>");


                    }

                    worker.EndWork(delegate ()
                    {
                        Viewer = new WebBrowserWindow();
                        Viewer.WindowState = System.Windows.WindowState.Maximized;
                        Viewer.Closing += delegate (object sender, CancelEventArgs e)
                        {
                            Viewer = null;
                        };
                        Viewer.Browser.Navigating += delegate (object sender, System.Windows.Navigation.NavigatingCancelEventArgs e)
                        {
                            if (e.Uri.Scheme == "command")
                            {
                                e.Cancel = true;
                                Viewer.StatusBar.StartWork("Following Hyperlink", delegate ()
                                {
                                    Action continuation;
                                    var message = DoCommand(e.Uri.LocalPath, Viewer.StatusBar, out continuation);
                                    Viewer.StatusBar.EndWork(delegate ()
                                    {
                                        if (message != null)
                                            Viewer.StatusBar.Log(message);
                                        if (continuation != null)
                                        {
                                            continuation();
                                        }
                                    });
                                });
                            }
                        };

                        Viewer.Width = 1000;
                        Viewer.Height = 600;
                        Viewer.Title = Title;
                        WebBrowserWindow.Navigate(Viewer.Browser, reportFileName);
                        Viewer.Show();

                        if (doAfter != null)
                            doAfter();
                    });
                });
            }
            else
            {
                Viewer.Focus();
                if (doAfter != null)
                    doAfter();
            }
        }

        public override void Close() { }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["HtmlReportBitmapImage"] as ImageSource; } }
    }

    public class PerfViewTraceInfo : PerfViewHtmlReport
    {
        public PerfViewTraceInfo(PerfViewFile dataFile) : base(dataFile, "TraceInfo") { }
        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log)
        {
            writer.WriteLine("<H2>Information on the Trace and Machine</H2>");
            writer.WriteLine("<Center>");
            writer.WriteLine("<Table Border=\"1\">");
            writer.WriteLine("<TR><TD>Machine Name</TD><TD Align=\"Center\">{0}</TD></TR>",
                string.IsNullOrEmpty(dataFile.MachineName) ? "&nbsp;" : dataFile.MachineName);
            writer.WriteLine("<TR><TD>Operating System</TD><TD Align=\"Center\">{0}</TD></TR>",
                string.IsNullOrEmpty(dataFile.OSName) ? "&nbsp;" : dataFile.OSName);
            writer.WriteLine("<TR><TD>OS Build Number</TD><TD Align=\"Center\">{0}</TD></TR>",
                string.IsNullOrEmpty(dataFile.OSBuild) ? "&nbsp;" : dataFile.OSBuild);
            writer.WriteLine("<TR><TD Title=\"This is negative if the data was collected in a time zone west of UTC\">UTC offset where data was collected</TD><TD Align=\"Center\">{0}</TD></TR>",
                dataFile.UTCOffsetMinutes.HasValue ? (dataFile.UTCOffsetMinutes.Value / 60.0).ToString("f2") : "Unknown");
            writer.WriteLine("<TR><TD Title=\"This is negative if PerfView is running in a time zone west of UTC\">UTC offset where PerfView is running</TD><TD Align=\"Center\">{0:f2}</TD></TR>",
                TimeZoneInfo.Local.GetUtcOffset(dataFile.SessionStartTime).TotalHours);

            if (dataFile.UTCOffsetMinutes.HasValue)
            {
                writer.WriteLine("<TR><TD Title=\"This is negative if analysis is happening west of collection\">Delta of Local and Collection Time</TD><TD Align=\"Center\">{0:f2}</TD></TR>",
                    TimeZoneInfo.Local.GetUtcOffset(dataFile.SessionStartTime).TotalHours - (dataFile.UTCOffsetMinutes.Value / 60.0));
            }
            writer.WriteLine("<TR><TD>OS Boot Time</TD><TD Align=\"Center\">{0:MM/dd/yyyy HH:mm:ss.fff}</TD></TR>", dataFile.BootTime);
            writer.WriteLine("<TR><TD>Trace Start Time</TD><TD Align=\"Center\">{0:MM/dd/yyyy HH:mm:ss.fff}</TD></TR>", dataFile.SessionStartTime);
            writer.WriteLine("<TR><TD>Trace End Time</TD><TD Align=\"Center\">{0:MM/dd/yyyy HH:mm:ss.fff}</TD></TR>", dataFile.SessionEndTime);
            writer.WriteLine("<TR><TD>Trace Duration (Sec)</TD><TD Align=\"Center\">{0:n1}</TD></TR>", dataFile.SessionDuration.TotalSeconds);
            writer.WriteLine("<TR><TD>CPU Frequency (Mhz)</TD><TD Align=\"Center\">{0:n0}</TD></TR>", dataFile.CpuSpeedMHz);
            writer.WriteLine("<TR><TD>Number Of Processors</TD><TD Align=\"Center\">{0}</TD></TR>", dataFile.NumberOfProcessors);
            writer.WriteLine("<TR><TD>Memory Size (Meg)</TD><TD Align=\"Center\">{0:n0}</TD></TR>", dataFile.MemorySizeMeg);
            writer.WriteLine("<TR><TD>Pointer Size</TD><TD Align=\"Center\">{0}</TD></TR>", dataFile.PointerSize);
            writer.WriteLine("<TR><TD>Sample Profile Interval (MSec) </TD><TD Align=\"Center\">{0:n2}</TD></TR>", dataFile.SampleProfileInterval.TotalMilliseconds);
            writer.WriteLine("<TR><TD>Total Events</TD><TD Align=\"Center\">{0:n0}</TD></TR>", dataFile.EventCount);
            writer.WriteLine("<TR><TD>Lost Events</TD><TD Align=\"Center\">{0:n0}</TD></TR>", dataFile.EventsLost);

            double len = 0;
            try
            {
                len = new System.IO.FileInfo(DataFile.FilePath).Length / 1000000.0;
            }
            catch (Exception) { }
            if (len > 0)
                writer.WriteLine("<TR><TD>ETL File Size (MB)</TD><TD Align=\"Center\">{0:n1}</TD></TR>", len);
            string logPath = null;
            int etlIdx = dataFile.FilePath.LastIndexOf(".etl", dataFile.FilePath.Length - 6);       // Start search right before .etlx
            if (0 <= etlIdx)
                logPath = dataFile.FilePath.Substring(0, etlIdx) + ".LogFile.txt";
            if (logPath != null && File.Exists(logPath))
                writer.WriteLine("<TR><TD colspan=\"2\" Align=\"Center\"> <A HREF=\"command:displayLog:{0}\">View data collection log file</A></TD></TR>", logPath);
            else
                writer.WriteLine("<TR><TD colspan=\"2\" Align=\"Center\"> No data collection log file found</A></TD></TR>", logPath);
            writer.WriteLine("</Table>");
            writer.WriteLine("</Center>");
        }

        protected override string DoCommand(string command, StatusBar worker)
        {

            if (command.StartsWith("displayLog:"))
            {
                string logFile = command.Substring(command.IndexOf(':') + 1);
                worker.Parent.Dispatcher.BeginInvoke((Action)delegate ()
                {
                    var logTextWindow = new Controls.TextEditorWindow();
                    logTextWindow.TextEditor.OpenText(logFile);
                    logTextWindow.TextEditor.IsReadOnly = true;
                    logTextWindow.Title = "Collection time log";
                    logTextWindow.HideOnClose = true;
                    logTextWindow.Show();
                    logTextWindow.TextEditor.Body.ScrollToEnd();
                });

                return "Displaying Log";
            }
            return "Unknown command " + command;
        }
    }
    /// <summary>
    /// Used to Display Processes Summary 
    /// </summary>
    public class PerfViewProcesses : PerfViewHtmlReport
    {

        public PerfViewProcesses(PerfViewFile dataFile) : base(dataFile, "Processes") { }
        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log)
        {
            m_processes = new List<TraceProcess>(dataFile.Processes);
            // Sort by CPU time (largest first), then by start time (latest first)
            m_processes.Sort(delegate (TraceProcess x, TraceProcess y)
            {
                var ret = y.CPUMSec.CompareTo(x.CPUMSec);
                if (ret != 0)
                    return ret;
                return y.StartTimeRelativeMsec.CompareTo(x.StartTimeRelativeMsec);
            });

            var shortProcs = new List<TraceProcess>();
            var longProcs = new List<TraceProcess>();
            foreach (var process in m_processes)
            {
                if (process.ProcessID < 0)
                    continue;
                if (process.StartTimeRelativeMsec == 0 &&
                    process.EndTimeRelativeMsec == dataFile.SessionEndTimeRelativeMSec)
                    longProcs.Add(process);
                else
                    shortProcs.Add(process);
            }

            writer.WriteLine("<H2>Process Summary</H2>");

            writer.WriteLine("<UL>");
            writer.WriteLine("<LI> <A HREF=\"command:processes\">View Process Data in Excel</A></LI>");
            writer.WriteLine("<LI> <A HREF=\"command:module\">View Process Modules in Excel</A></LI>");
            writer.WriteLine("</UL>");

            if (shortProcs.Count > 0)
            {
                writer.WriteLine("<H3>Processes that did <strong>not</strong> live for the entire trace.</H3>");
                WriteProcTable(writer, shortProcs, true);
            }
            if (longProcs.Count > 0)
            {
                writer.WriteLine("<H3>Processes that <strong>did</strong> live for the entire trace.</H3>");
                WriteProcTable(writer, longProcs, false);
            }
        }
        /// <summary>
        /// Takes in either "processes" or "module" which will make a csv of their respective format
        /// </summary>
        protected override string DoCommand(string command, StatusBar worker)
        {
            if (command == "processes")
            {
                var csvFile = CacheFiles.FindFile(FilePath, ".processesSummary.csv");
                if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                    File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                {
                    this.MakeProcessesCsv(m_processes, csvFile);
                }
                Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                return "Opening CSV " + csvFile;
            }
            else if (command == "module")
            {
                var csvFile = CacheFiles.FindFile(FilePath, ".processesModule.csv");
                if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                    File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                {
                    this.MakeModuleCsv(m_processes, csvFile);
                }
                Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                return "Opening CSV " + csvFile;
            }
            return null;
        }
        #region private
        private void WriteProcTable(TextWriter writer, List<TraceProcess> processes, bool showExit)
        {
            bool showBitness = false;
            if (processes.Count > 0)
                showBitness = (processes[0].Log.PointerSize == 8);

            writer.WriteLine("<Table Border=\"1\">");
            writer.Write("<TR>");
            writer.Write("<TH Align=\"Center\">Name</TH>");
            writer.Write("<TH Align=\"Center\">ID</TH>");
            writer.Write("<TH Align=\"Center\">Parent<BR/>ID</TH>");
            if (showBitness)
                writer.Write("<TH Align=\"Center\">Bitness</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The amount of CPU time used (on any processor).\" >CPU<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The CPU used divided by the duration.\">Ave Procs<BR/>Used</TH>");
            if (showExit)
            {
                writer.Write("<TH Align=\"Center\">Duration<BR/>MSec</TH>");
                writer.Write("<TH Align=\"Center\" Title=\"The start time in milliseconds from the time the trace started.\">Start<BR/>MSec</TH>");
                writer.Write("<TH Align=\"Center\" Title=\"The integer that the process returned when it exited.\">Exit<BR/>Code</TH>");
            }
            writer.Write("<TH Align=\"Center\">Command Line</TH>");
            writer.WriteLine("</TR>");
            foreach (TraceProcess process in processes)
            {
                writer.Write("<TR>");
                writer.Write("<TD Align=\"Left\">{0}</TD>", process.Name);
                writer.Write("<TD Align=\"Right\">{0}</TD>", process.ProcessID);
                writer.Write("<TD Align=\"Right\">{0}</TD>", process.ParentID);
                if (showBitness)
                    writer.Write("<TD Align=\"Center\">{0}</TD>", process.Is64Bit ? 64 : 32);
                writer.Write("<TD Align=\"Right\">{0:n0}</TD>", process.CPUMSec);
                writer.Write("<TD Align=\"Right\">{0:n3}</TD>", process.CPUMSec / (process.EndTimeRelativeMsec - process.StartTimeRelativeMsec));
                if (showExit)
                {
                    writer.Write("<TD Align=\"Right\">{0:n3}</TD>", process.EndTimeRelativeMsec - process.StartTimeRelativeMsec);
                    writer.Write("<TD Align=\"Right\">{0:n3}</TD>", process.StartTimeRelativeMsec);
                    writer.Write("<TD Align=\"Right\">{0}</TD>", process.ExitStatus.HasValue ? "0x" + process.ExitStatus.Value.ToString("x") : "?");
                }
                writer.Write("<TD Align=\"Left\">{0}</TD>", process.CommandLine);
                writer.WriteLine("</TR>");
            }
            writer.WriteLine("</Table>");
        }

        /// <summary>
        /// Makes a csv file of the contents or processes at the filepath. 
        /// Headers to csv are  Name,ID,Parent_ID,Bitness,CPUMsec,AveProcsUsed,DurationMSec,StartMSec,ExitCode,CommandLine
        /// </summary>
        private void MakeProcessesCsv(List<TraceProcess> processes, string filepath)
        {
            using (var writer = File.CreateText(filepath))
            {
                //add headers 
                string listSeparator = Thread.CurrentThread.CurrentCulture.TextInfo.ListSeparator;
                writer.WriteLine("Name{0}ID{0}Parent_ID{0}Bitness{0}CPUMsec{0}AveProcsUsed{0}DurationMSec{0}StartMSec{0}ExitCode{0}CommandLine", listSeparator);
                foreach (TraceProcess process in processes)
                {
                    writer.Write("{0}{1}", process.Name, listSeparator);
                    writer.Write("{0}{1}", process.ProcessID, listSeparator);
                    writer.Write("{0}{1}", process.ParentID, listSeparator);
                    writer.Write("{0}{1}", process.Is64Bit ? 64 : 32, listSeparator);
                    writer.Write("{0:f0}{1}", process.CPUMSec, listSeparator);
                    writer.Write("{0:f3}{1}", process.CPUMSec / (process.EndTimeRelativeMsec - process.StartTimeRelativeMsec), listSeparator);
                    writer.Write("{0:f3}{1}", process.EndTimeRelativeMsec - process.StartTimeRelativeMsec, listSeparator);
                    writer.Write("{0:f3}{1}", process.StartTimeRelativeMsec, listSeparator);
                    writer.Write("{0}{1}", process.ExitStatus.HasValue ? "0x" + process.ExitStatus.Value.ToString("x") : "?", listSeparator);
                    writer.Write("{0}", PerfViewExtensibility.Events.EscapeForCsv(process.CommandLine, listSeparator));
                    writer.WriteLine("");
                }
            }
        }
        /// <summary>
        /// Makes a Csv at filepath
        /// </summary>
        private void MakeModuleCsv(List<TraceProcess> processes, string filepath)
        {
            using (var writer = File.CreateText(filepath))
            {
                //add headers 
                string listSeparator = Thread.CurrentThread.CurrentCulture.TextInfo.ListSeparator;
                writer.WriteLine("ProcessName{0}ProcessID{0}Name{0}FileVersion{0}BuildTime{0}FilePath", listSeparator);
                foreach (TraceProcess process in processes)  //turn into private function 
                {
                    foreach (TraceLoadedModule module in process.LoadedModules)
                    {
                        writer.Write("{0}{1}", process.Name, listSeparator);
                        writer.Write("{0}{1}", process.ProcessID, listSeparator);
                        writer.Write("{0}{1}", module.ModuleFile.Name, listSeparator);
                        writer.Write("{0}{1}", module.ModuleFile.FileVersion, listSeparator);
                        writer.Write("{0}{1}", PerfViewExtensibility.Events.EscapeForCsv(module.ModuleFile.BuildTime.ToString(), listSeparator), listSeparator);
                        writer.Write("{0}", module.ModuleFile.FilePath);
                        writer.WriteLine();
                    }
                }
            }
        }

        /// <summary>
        /// All the processes in this view.  
        /// </summary>
        private List<TraceProcess> m_processes;
        #endregion
    }

    public class PerfViewAspNetStats : PerfViewHtmlReport
    {
        public PerfViewAspNetStats(PerfViewFile dataFile) : base(dataFile, "Asp.Net Stats") { }
        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log)
        {
            var dispatcher = dataFile.Events.GetSource();
            var aspNet = new AspNetTraceEventParser(dispatcher);

            m_requests = new List<AspNetRequest>();
            var requestByID = new Dictionary<Guid, AspNetRequest>();

            var startIntervalMSec = 0;
            var totalIntervalMSec = dataFile.SessionDuration.TotalMilliseconds;

            var bucketIntervalMSec = 1000;
            var numBuckets = Math.Max(1, (int)(totalIntervalMSec / bucketIntervalMSec));

            var GCType = "Unknown";
            var requestsRecieved = 0;

            var byTimeStats = new ByTimeRequestStats[numBuckets];
            for (int i = 0; i < byTimeStats.Length; i++)
                byTimeStats[i] = new ByTimeRequestStats();

            var requestsProcessing = 0;

            dispatcher.Kernel.PerfInfoSample += delegate (SampledProfileTraceData data)
            {
                if (data.ProcessID == 0)    // Non-idle time.  
                    return;

                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                    byTimeStats[idx].CpuMSec++;
            };

            dispatcher.Clr.RuntimeStart += delegate (RuntimeInformationTraceData data)
            {
                if ((data.StartupFlags & StartupFlags.SERVER_GC) != 0)
                    GCType = "Server";
                else
                    GCType = "Client";
            };

            dispatcher.Clr.ContentionStart += delegate (ContentionTraceData data)
            {
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                    byTimeStats[idx].Contentions++;
            };

            dispatcher.Clr.GCStop += delegate (GCEndTraceData data)
            {
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                {
                    byTimeStats[idx].NumGcs++;
                    if (data.Depth >= 2)
                        byTimeStats[idx].NumGen2Gcs++;
                }
            };

            bool seenBadAllocTick = false;
            dispatcher.Clr.GCAllocationTick += delegate (GCAllocationTickTraceData data)
            {
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                {
                    var valueMB = data.GetAllocAmount(ref seenBadAllocTick) / 1000000.0f;

                    byTimeStats[idx].GCHeapAllocMB += valueMB;
                }
            };

            dispatcher.Clr.GCHeapStats += delegate (GCHeapStatsTraceData data)
            {
                // TODO should it be summed over processes? 
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                {
                    var totalSize = data.GenerationSize0 + data.GenerationSize1 + data.GenerationSize2 + data.GenerationSize3;
                    byTimeStats[idx].GCHeapSizeMB = Math.Max(byTimeStats[idx].GCHeapSizeMB, totalSize / 1000000.0F);
                }
            };

            dispatcher.Clr.ThreadPoolWorkerThreadAdjustmentAdjustment += delegate (ThreadPoolWorkerThreadAdjustmentTraceData data)
            {
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                {
                    // TODO compute the average weighted by time.  
                    byTimeStats[idx].ThreadPoolThreadCountSum += data.NewWorkerThreadCount;
                    byTimeStats[idx].ThreadPoolAdjustmentCount++;
                }
            };

            var lastDiskEndMSec = new GrowableArray<double>(4);
            dispatcher.Kernel.AddCallbackForEvents<DiskIOTraceData>(delegate (DiskIOTraceData data)
            {
                // Compute the disk service time.  
                if (data.DiskNumber >= lastDiskEndMSec.Count)
                    lastDiskEndMSec.Count = data.DiskNumber + 1;

                var elapsedMSec = data.ElapsedTimeMSec;
                double serviceTimeMSec = elapsedMSec;
                double durationSinceLastIOMSec = data.TimeStampRelativeMSec - lastDiskEndMSec[data.DiskNumber];
                if (durationSinceLastIOMSec < serviceTimeMSec)
                    serviceTimeMSec = durationSinceLastIOMSec;

                // Add it to the stats.  
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                    byTimeStats[idx].DiskIOMsec += serviceTimeMSec;
            });

            dispatcher.Kernel.ThreadCSwitch += delegate (CSwitchTraceData data)
            {
                int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                if (idx >= 0)
                    byTimeStats[idx].ContextSwitch++;
            };

            aspNet.AspNetReqStart += delegate (AspNetStartTraceData data)
            {
                var request = new AspNetRequest();
                request.ID = data.ContextId;
                request.Path = data.Path;
                request.Method = data.Method;
                request.QueryString = data.QueryString;
                request.StartTimeRelativeMSec = data.TimeStampRelativeMSec;
                request.StartThreadID = data.ThreadID;
                request.Process = data.Process();

                requestByID[request.ID] = request;
                m_requests.Add(request);

                requestsRecieved++;
                request.RequestsReceived = requestsRecieved;
                request.RequestsProcessing = requestsProcessing;
            };

            aspNet.AspNetReqStop += delegate (AspNetStopTraceData data)
            {
                AspNetRequest request;
                if (requestByID.TryGetValue(data.ContextId, out request))
                {
                    // If we missed the hander end, then complete it.  
                    if (request.HandlerStartTimeRelativeMSec > 0 && request.HandlerStopTimeRelativeMSec == 0)
                    {
                        --requestsProcessing;
                        request.HandlerStopTimeRelativeMSec = data.TimeStampRelativeMSec;
                        Debug.Assert(requestsProcessing >= 0);
                    }

                    Debug.Assert(request.StopTimeRelativeMSec == 0);
                    request.StopTimeRelativeMSec = data.TimeStampRelativeMSec;
                    request.StopThreadID = data.ThreadID;
                    Debug.Assert(request.StopTimeRelativeMSec > request.StartTimeRelativeMSec);

                    --requestsRecieved;
                    Debug.Assert(requestsRecieved >= 0);

                    int idx = GetBucket(data.TimeStampRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                    if (idx >= 0)
                    {
                        var byTimeState = byTimeStats[idx];
                        byTimeState.NumRequests++;
                        byTimeState.DurationMSecTotal += (float)request.DurationMSec;
                        byTimeState.QueuedDurationMSecTotal += (float)request.QueueDurationMSec;
                        if ((float)request.DurationMSec > byTimeState.RequestsMSecMax)
                        {
                            byTimeState.RequestsThreadOfMax = request.HandlerThreadID;
                            byTimeState.RequestsTimeOfMax = request.StartTimeRelativeMSec;
                            byTimeState.RequestsMSecMax = (float)request.DurationMSec;
                        }
                    }
                }
                else
                    log.WriteLine("WARNING: stop event without a start at {0:n3} Msec.", data.TimeStampRelativeMSec);
            };


            Action<int, double, Guid> handlerStartAction = delegate (int threadID, double timeStampRelativeMSec, Guid contextId)
            {
                AspNetRequest request;
                if (requestByID.TryGetValue(contextId, out request))
                {
                    // allow this routine to be called twice for the same event.  
                    if (request.HandlerStartTimeRelativeMSec != 0)
                        return;
                    Debug.Assert(request.StopTimeRelativeMSec == 0);

                    request.HandlerStartTimeRelativeMSec = timeStampRelativeMSec;
                    request.HandlerThreadID = threadID;

                    requestsProcessing++;
                    Debug.Assert(requestsProcessing <= requestsRecieved);
                }
            };

            aspNet.AspNetReqStartHandler += delegate (AspNetStartHandlerTraceData data)
            {
                handlerStartAction(data.ThreadID, data.TimeStampRelativeMSec, data.ContextId);
            };

            // When you don't turn on the most verbose ASP.NET events, you only get a SessionDataBegin event.  Use
            // this as the start of the processing (because it is pretty early) if we have nothing better to use.  
            aspNet.AspNetReqSessionDataBegin += delegate (AspNetAcquireSessionBeginTraceData data)
            {
                handlerStartAction(data.ThreadID, data.TimeStampRelativeMSec, data.ContextId);
            };

            aspNet.AspNetReqEndHandler += delegate (AspNetEndHandlerTraceData data)
            {
                AspNetRequest request;
                if (requestByID.TryGetValue(data.ContextId, out request))
                {
                    if (request.HandlerStartTimeRelativeMSec > 0 && request.HandlerStopTimeRelativeMSec == 0)            // If we saw the start 
                    {
                        --requestsProcessing;
                        request.HandlerStopTimeRelativeMSec = data.TimeStampRelativeMSec;
                    }
                    Debug.Assert(requestsProcessing >= 0);
                }
            };

            dispatcher.Process();
            requestByID = null;         // We are done with the table

            var globalMaxRequestsReceived = 0;
            var globalMaxRequestsQueued = 0;
            var globalMaxRequestsProcessing = 0;

            // It is not uncommon for there to be missing end events, etc, which mess up the running counts of 
            // what is being processed.   Thus look for these messed up events and fix them.  Once the counts
            // are fixed use them to compute the number queued and number being processed over the interval.  
            int recAdjust = 0;
            int procAdjust = 0;
            foreach (var req in m_requests)
            {
                // Compute the fixup for the all subsequent request.  
                Debug.Assert(0 < req.StartTimeRelativeMSec);         // we always set the start time. 

                // Throw out receive counts that don't have a end event
                if (req.StopTimeRelativeMSec == 0)
                    recAdjust++;

                // Throw out process counts that don't have a stop handler or a stop.   
                if (0 < req.HandlerStartTimeRelativeMSec && (req.HandlerStopTimeRelativeMSec == 0 || req.StopTimeRelativeMSec == 0))
                    procAdjust++;

                // Fix up the requests 
                req.RequestsReceived -= recAdjust;
                req.RequestsProcessing -= procAdjust;

                Debug.Assert(0 <= req.RequestsReceived);
                Debug.Assert(0 <= req.RequestsProcessing);
                Debug.Assert(0 <= req.RequestsQueued);
                Debug.Assert(req.RequestsQueued <= req.RequestsReceived);

                // A this point req is accurate.   Calcuate global and byTime stats from that.  
                if (globalMaxRequestsReceived < req.RequestsReceived)
                    globalMaxRequestsReceived = req.RequestsReceived;

                if (globalMaxRequestsProcessing < req.RequestsProcessing)
                    globalMaxRequestsProcessing = req.RequestsProcessing;

                var requestsQueued = req.RequestsQueued;
                if (globalMaxRequestsQueued < requestsQueued)
                    globalMaxRequestsQueued = requestsQueued;

                if (req.StopTimeRelativeMSec > 0)
                {
                    int idx = GetBucket(req.StopTimeRelativeMSec, startIntervalMSec, bucketIntervalMSec, byTimeStats.Length);
                    if (idx >= 0)
                    {
                        byTimeStats[idx].MinRequestsQueued = Math.Min(byTimeStats[idx].MinRequestsQueued, requestsQueued);
                        byTimeStats[idx].MeanRequestsProcessingSum += req.RequestsProcessing;
                        byTimeStats[idx].MeanRequestsProcessingCount++;
                    }
                }
            }
            if (recAdjust != 0)
                log.WriteLine("There were {0} event starts without a matching event end in the trace", recAdjust);
            if (procAdjust != 0)
                log.WriteLine("There were {0} handler starts without a matching handler end in the trace", procAdjust);

            writer.WriteLine("<H2>ASP.Net Statistics</H2>");
            writer.WriteLine("<UL>");
            var fileInfo = new System.IO.FileInfo(dataFile.FilePath);
            writer.WriteLine("<LI> Total Requests: {0:n} </LI>", m_requests.Count);
            writer.WriteLine("<LI> Trace Duration (Sec): {0:n1} </LI>", dataFile.SessionDuration.TotalSeconds);
            writer.WriteLine("<LI> Average Request/Sec: {0:n2} </LI>", m_requests.Count / dataFile.SessionDuration.TotalSeconds);
            writer.WriteLine("<LI> Number of CPUs: {0}</LI>", dataFile.NumberOfProcessors);
            writer.WriteLine("<LI> Maximum Number of requests recieved but not replied to: {0}</LI>", globalMaxRequestsReceived);
            writer.WriteLine("<LI> Maximum Number of requests queued waiting for processing: {0}</LI>", globalMaxRequestsQueued);
            writer.WriteLine("<LI> Maximum Number of requests concurrently being worked on: {0}</LI>", globalMaxRequestsProcessing);
            writer.WriteLine("<LI> Total Memory (Meg): {0:n0}</LI>", dataFile.MemorySizeMeg);
            writer.WriteLine("<LI> GC Kind: {0} </LI>", GCType);
            writer.WriteLine("<LI> <A HREF=\"#rollupPerTime\">Rollup over time</A></LI>");
            writer.WriteLine("<LI> <A HREF=\"#rollupPerRequestType\">Rollup per request type</A></LI>");
            writer.WriteLine("<LI> <A HREF=\"command:excel/requests\">View ALL individual requests in Excel</A></LI>");
            writer.WriteLine("</UL>");

            writer.Write("<P><A ID=\"rollupPerTime\">Statistics over time.  Hover over column headings for explaination of columns.</A></P>");
            writer.WriteLine("<Table Border=\"1\">");
            writer.Write("<TR>");
            writer.Write("<TH Align=\"Center\">Time Interval MSec</TH>");
            writer.Write("<TH Align=\"Center\">Req/Sec</TH>");
            writer.Write("<TH Align=\"Center\">Max Resp<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The start time of the maximum response (may preceed bucket start)\">Start of<BR/>Max</TH>");
            writer.Write("<TH Align=\"Center\">Thread of<BR/>Max</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The time from when the response is read from the OS until we have written a reply.\">Mean Resp<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The time a request waits before processing begins.\">Mean Queue<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The minium number of requests that have been recieved but not yet processed.\">Min<BR>Queued</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The average number of requests that are actively being processed simultaneously.\">Mean<BR>Proc</TH>");
            writer.Write("<TH Align=\"Center\">CPU %</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The number of context switches per second.\">CSwitch / Sec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The total amount of time (MSec) the disk was active (all disks), machine wide.\">Disk<BR>MSec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The average number of thread-pool worker over this time period\">Thread<BR>Workers</TH>");
            writer.Write("<TH Align=\"Center\">GC Alloc<BR/>MB/Sec</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The maximum of the GC heap size (in any process) after any GC\">GC Heap<BR/>MB</TH>");
            writer.Write("<TH Align=\"Center\">GCs</TH>");
            writer.Write("<TH Align=\"Center\">Gen2<BR/>GCs</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The number of times one thread had to wait for another thread because of a .NET lock\">.NET<BR/>Contention</TH>");
            writer.WriteLine("</TR>");

            // Rollup by time 

            // Only print until CPU goes to 0.  This is because the kernel events stop sooner, and it is confusing 
            // to have one without the other 
            var limit = numBuckets;
            while (0 < limit && byTimeStats[limit - 1].CpuMSec == 0)
                --limit;
            if (limit == 0)             // Something went wrong (e.g no CPU sampling turned on), give up on trimming.
                limit = numBuckets;

            bool wroteARow = false;
            for (int i = 0; i < limit; i++)
            {
                var byTimeStat = byTimeStats[i];
                if (byTimeStat.NumRequests == 0 && !wroteARow)       // Skip initial cases if any. 
                    continue;
                wroteARow = true;
                var startBucketMSec = startIntervalMSec + i * bucketIntervalMSec;
                writer.Write("<TR>");
                writer.Write("<TD Align=\"Center\">{0:n0} - {1:n0}</TD>", startBucketMSec, startBucketMSec + bucketIntervalMSec);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.NumRequests / (bucketIntervalMSec / 1000.0));
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.RequestsMSecMax);
                writer.Write("<TD Align=\"Center\">{0:n3}</TD>", byTimeStat.RequestsTimeOfMax);
                writer.Write("<TD Align=\"Center\">{0}</TD>", byTimeStat.RequestsThreadOfMax);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.DurationMSecTotal / byTimeStat.NumRequests);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.QueuedDurationMSecTotal / byTimeStat.NumRequests);
                writer.Write("<TD Align=\"Center\">{0}</TD>", (byTimeStat.MinRequestsQueued == int.MaxValue) ? 0 : byTimeStat.MinRequestsQueued - 1);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.MeanRequestsProcessing);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", byTimeStat.CpuMSec * 100.0 / (dataFile.NumberOfProcessors * bucketIntervalMSec));
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", byTimeStat.ContextSwitch / (bucketIntervalMSec / 1000.0));
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.DiskIOMsec);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.MeanThreadPoolThreads);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", byTimeStat.GCHeapAllocMB / (bucketIntervalMSec / 1000.0));
                writer.Write("<TD Align=\"Center\">{0}</TD>", byTimeStat.GCHeapSizeMB == 0 ? "No GCs" : byTimeStat.GCHeapSizeMB.ToString("f3"));
                writer.Write("<TD Align=\"Center\">{0}</TD>", byTimeStat.NumGcs);
                writer.Write("<TD Align=\"Center\">{0}</TD>", byTimeStat.NumGen2Gcs);
                writer.Write("<TD Align=\"Center\">{0}</TD>", byTimeStat.Contentions);
                writer.WriteLine("</TR>");
            }
            writer.WriteLine("</Table>");

            var byRequestType = new Dictionary<string, ByRequestStats>();
            foreach (var request in m_requests)
            {
                // Skip requests that did not finish.  
                if (request.StopTimeRelativeMSec == 0)
                    continue;

                var key = request.Method + request.Path + request.QueryString;
                ByRequestStats stats;
                if (!byRequestType.TryGetValue(key, out stats))
                    byRequestType.Add(key, new ByRequestStats(request));
                else
                    stats.AddRequest(request);
            }

            var requestStats = new List<ByRequestStats>(byRequestType.Values);
            requestStats.Sort(delegate (ByRequestStats x, ByRequestStats y)
            {
                return -x.TotalDurationMSec.CompareTo(y.TotalDurationMSec);
            });

            // Rollup by kind of kind of page request
            writer.Write("<P><A ID=\"rollupPerRequestType\">Statistics Per Request URL</A></P>");
            writer.WriteLine("<Table Border=\"1\">");
            writer.Write("<TR>");
            writer.Write("<TH Align=\"Center\">Method</TH>");
            writer.Write("<TH Align=\"Center\">Path</TH>");
            writer.Write("<TH Align=\"Center\">Query String</TH>");
            writer.Write("<TH Align=\"Center\">Num</TH>");
            writer.Write("<TH Align=\"Center\">Num<BR/>&gt; 1s</TH>");
            writer.Write("<TH Align=\"Center\">Num<BR/>&gt; 5s</TH>");
            writer.Write("<TH Align=\"Center\">Num<BR/>&gt; 10s</TH>");
            writer.Write("<TH Align=\"Center\">Total<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\">Mean Resp<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\">Max Resp<BR/>MSec</TH>");
            writer.Write("<TH Align=\"Center\">Start of<BR/>Max</TH>");
            writer.Write("<TH Align=\"Center\">End of<BR/>Max</TH>");
            writer.Write("<TH Align=\"Center\">Thread of<BR/>Max</TH>");
            writer.WriteLine("</TR>");

            foreach (var requestStat in requestStats)
            {
                writer.Write("<TR>");
                writer.Write("<TD Align=\"Center\">{0}</TD>", requestStat.MaxRequest.Method);
                writer.Write("<TD Align=\"Center\">{0}</TD>", requestStat.MaxRequest.Path);
                var queryString = requestStat.MaxRequest.QueryString;
                if (string.IsNullOrWhiteSpace(queryString))
                    queryString = "&nbsp;";
                writer.Write("<TD Align=\"Center\">{0}</TD>", queryString);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", requestStat.NumRequests);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", requestStat.NumRequest1Sec);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", requestStat.NumRequest5Sec);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", requestStat.NumRequest10Sec);
                writer.Write("<TD Align=\"Center\">{0:n0}</TD>", requestStat.TotalDurationMSec);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", requestStat.MeanRequestMSec);
                writer.Write("<TD Align=\"Center\">{0:n1}</TD>", requestStat.MaxRequest.DurationMSec);
                writer.Write("<TD Align=\"Center\">{0:n3}</TD>", requestStat.MaxRequest.StartTimeRelativeMSec);
                writer.Write("<TD Align=\"Center\">{0:n3}</TD>", requestStat.MaxRequest.StopTimeRelativeMSec);
                writer.Write("<TD Align=\"Center\">{0}</TD>", requestStat.MaxRequest.HandlerThreadID);
                writer.WriteLine("</TR>");
            }
            writer.WriteLine("</Table>");
            // create some whitespace at the end 
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
            writer.WriteLine("<p>&nbsp;</p>");
        }

        protected override string DoCommand(string command, StatusBar worker)
        {
            if (command.StartsWith("excel/"))
            {
                var rest = command.Substring(6);
                if (rest == "requests")
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".aspnet.requests.csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                    {
                        CreateCSVFile(m_requests, csvFile);
                    }
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV file on " + csvFile;
                }
            }
            return "Unknown command " + command;
        }

        #region private
        class AspNetRequest
        {
            public TraceProcess Process;
            public double DurationMSec { get { return Math.Max(StopTimeRelativeMSec - StartTimeRelativeMSec, 0); } }

            public double QueueDurationMSec
            {
                get
                {
                    // Missing Handler events can cause this.  Typically they are the first events in the system.
                    // TODO is this too misleading?  
                    if (!(HandlerStartTimeRelativeMSec >= StartTimeRelativeMSec))
                        return 0;
                    return HandlerStartTimeRelativeMSec - StartTimeRelativeMSec;
                }
            }
            public int StartThreadID;       // TODO remove?  not clear it is interesting. 
            public double StartTimeRelativeMSec;

            public int StopThreadID;        // TODO remove?  not clear it is interesting. 
            public double StopTimeRelativeMSec;

            public int HandlerThreadID;
            public double HandlerStartTimeRelativeMSec;
            public double HandlerStopTimeRelativeMSec;
            public double HandlerDurationMSec { get { return HandlerStopTimeRelativeMSec - HandlerStartTimeRelativeMSec; } }

            public int RequestsReceived;            // just after this request was received, how many have we received but not replied to?
            public int RequestsProcessing;          // just after this request was received, how many total requests are being processed.  
            public int RequestsQueued { get { return RequestsReceived - RequestsProcessing; } }

            public string Method;       // GET or POST
            public string Path;         // url path
            public string QueryString;  // Query 
            public Guid ID;
        }

        class ByTimeRequestStats
        {
            public ByTimeRequestStats()
            {
                MinRequestsQueued = int.MaxValue;
            }
            public int NumRequests;
            public int CpuMSec;
            public int ContextSwitch;
            public double DiskIOMsec;         // The amount of Disk service time (all disks, machine wide).  

            public float RequestsMSecMax;
            public double RequestsTimeOfMax;
            public int RequestsThreadOfMax;

            public float DurationMSecTotal;
            public float QueuedDurationMSecTotal;

            public int ThreadPoolThreadCountSum;
            public int ThreadPoolAdjustmentCount;
            public float MeanThreadPoolThreads { get { return (float)ThreadPoolThreadCountSum / ThreadPoolAdjustmentCount; } }

            public float GCHeapAllocMB;
            public float GCHeapSizeMB;
            public float NumGcs;
            public float NumGen2Gcs;
            public int Contentions;
            public int MinRequestsQueued;
            public float MeanRequestsProcessing { get { return MeanRequestsProcessingSum / MeanRequestsProcessingCount; } }

            internal float MeanRequestsProcessingSum;
            internal int MeanRequestsProcessingCount;
        };

        class ByRequestStats
        {
            public ByRequestStats(AspNetRequest request)
            {
                MaxRequest = request;
                AddRequest(request);
            }
            public void AddRequest(AspNetRequest request)
            {
                if (request.DurationMSec > MaxRequest.DurationMSec)
                    MaxRequest = request;
                TotalDurationMSec += request.DurationMSec;
                Debug.Assert(request.DurationMSec >= 0);
                Debug.Assert(TotalDurationMSec >= 0);
                NumRequests++;
                if (request.DurationMSec > 1000)
                    NumRequest1Sec++;
                if (request.DurationMSec > 5000)
                    NumRequest5Sec++;
                if (request.DurationMSec > 10000)
                    NumRequest10Sec++;
            }
            public double MeanRequestMSec { get { return TotalDurationMSec / NumRequests; } }

            public int NumRequest1Sec;
            public int NumRequest5Sec;
            public int NumRequest10Sec;

            public AspNetRequest MaxRequest;
            public double TotalDurationMSec;
            public int NumRequests;
        }

        private static int GetBucket(double timeStampMSec, int startIntervalMSec, int bucketIntervalMSec, int maxBucket)
        {
            if (timeStampMSec < startIntervalMSec)
                return -1;
            int idx = (int)(timeStampMSec / bucketIntervalMSec);
            if (idx >= maxBucket)
                return -1;
            return idx;
        }


        private void CreateCSVFile(List<AspNetRequest> requests, string csvFileName)
        {
            using (var csvFile = File.CreateText(csvFileName))
            {
                string listSeparator = Thread.CurrentThread.CurrentCulture.TextInfo.ListSeparator;
                csvFile.WriteLine("Method{0}Path{0}QueryString{0}StartMSec{0}DurationMSec{0}ProcStartMSec{0}ProcessingMSec{0}ProcessID{0}ProcThread{0}Received{0}Processing{0}Queued", listSeparator);
                foreach (var request in requests)
                {
                    if (request.StopTimeRelativeMSec == 0)       // Skip incomplete entries
                        continue;

                    csvFile.WriteLine("{1}{0}{2}{0}{3}{0}{4:f3}{0}{5:f2}{0}{6:f3}{0}{7:f2}{0}{8}{0}{9}{0}{10}{0}{11}{0}{12}", listSeparator,
                        request.Method, EventWindow.EscapeForCsv(request.Path, ","), EventWindow.EscapeForCsv(request.QueryString, ","),
                        request.StartTimeRelativeMSec, request.DurationMSec, request.HandlerStartTimeRelativeMSec, request.HandlerDurationMSec,
                        (request.Process != null) ? request.Process.ProcessID : 0, request.HandlerThreadID, request.RequestsReceived,
                        request.RequestsProcessing, request.RequestsQueued);
                }
            }
        }

        List<AspNetRequest> m_requests;
        #endregion
    }

    public class PerfViewEventStats : PerfViewHtmlReport
    {
        public PerfViewEventStats(PerfViewFile dataFile) : base(dataFile, "EventStats") { }
        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log)
        {
            m_counts = new List<TraceEventCounts>(dataFile.Stats);
            // Sort by count
            m_counts.Sort((x, y) => y.Count - x.Count);
            writer.WriteLine("<H2>Event Statistics</H2>");
            writer.WriteLine("<UL>");
            writer.WriteLine("<LI> <A HREF=\"command:excel\">View Event Statistics in Excel</A></LI>");
            writer.WriteLine("<LI>Total Event Count = {0:n0}</LI>", dataFile.EventCount);
            writer.WriteLine("<LI>Total Lost Events = {0}</LI>", dataFile.EventsLost);
            writer.WriteLine("</UL>");

            writer.WriteLine("<Table Border=\"1\">");
            writer.Write("<TR>");
            writer.Write("<TH Align=\"Center\">Name</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The number of times this event occurs in the log.\">Count</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The average size of just the payload of this event.\">Average<BR/>Data Size</TH>");
            writer.Write("<TH Align=\"Center\" Title=\"The number of times this event has a stack trace associated with it.\">Stack<BR/>Count</TH>");
            writer.WriteLine("</TR>");
            foreach (TraceEventCounts count in m_counts)
            {
                writer.Write("<TR>");
                writer.Write("<TD Align=\"Left\">{0}/{1}</TD>", count.ProviderName, count.EventName);
                writer.Write("<TD Align=\"Right\">{0:n0}</TD>", count.Count);
                writer.Write("<TD Align=\"Right\">{0:n0}</TD>", count.AveragePayloadSize);
                writer.Write("<TD Align=\"Right\">{0:n0}</TD>", count.StackCount);
                writer.WriteLine("</TR>");
            }
            writer.WriteLine("</Table>");
        }
        protected override string DoCommand(string command, StatusBar worker)
        {
            if (command == "excel")
            {
                var csvFile = CacheFiles.FindFile(FilePath, ".eventstats.csv");
                if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                    File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                {
                    //make the csv
                    this.MakeEventStatCsv(m_counts, csvFile);
                }
                Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                return "Opening CSV " + csvFile;
            }
            return null;
        }
        #region private
        private List<TraceEventCounts> m_counts;
        private void MakeEventStatCsv(List<TraceEventCounts> trace, string filepath)
        {
            string listSeparator = Thread.CurrentThread.CurrentCulture.TextInfo.ListSeparator;
            using (var writer = File.CreateText(filepath))
            {
                writer.WriteLine("Name{0}Count{0}AverageSize{0}StackCount", listSeparator);
                foreach (TraceEventCounts count in trace)
                {
                    writer.Write("{0}/{1}{2}", count.ProviderName, count.EventName, listSeparator);
                    writer.Write("{0}{1}", count.Count, listSeparator);
                    writer.Write("{0:f0}{1}", count.AveragePayloadSize, listSeparator);
                    writer.WriteLine("{0}", count.StackCount);
                }
            }
        }
        #endregion
    }

    public class PerfViewGCStats : PerfViewHtmlReport
    {
        public PerfViewGCStats(PerfViewFile dataFile) : base(dataFile, "GCStats") { }
        protected override string DoCommand(string command, StatusBar worker)
        {
            if (command.StartsWith("excel/"))
            {
                string raw = "";
                var rest = command.Substring(6);
                if (rest.StartsWith("perGeneration/"))
                {
                    raw = ".perGen";
                    rest = rest.Substring(14);
                }
                var processId = int.Parse(rest);
                GCProcess gcProc;
                if (m_gcStats.TryGetByID(processId, out gcProc))
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".gcStats." + processId.ToString() + raw + ".csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                    {
                        if (raw.Length != 0)
                            gcProc.PerGenerationCsv(csvFile);
                        else
                            gcProc.ToCsv(csvFile);
                    }
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV " + csvFile;
                }
            }
            else if (command.StartsWith("excelFinalization/"))
            {
                var processId = int.Parse(command.Substring(18));
                GCProcess gcProc;
                if (m_gcStats.TryGetByID(processId, out gcProc))
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".gcStats.Finalization." + processId.ToString() + ".csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                    {
                        gcProc.ToCsvFinalization(csvFile);
                    }
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV " + csvFile;
                }
            }
            else if (command.StartsWith("xml/"))
            {
                var processId = int.Parse(command.Substring(4));
                GCProcess gcProc;
                if (m_gcStats.TryGetByID(processId, out gcProc) && gcProc.m_detailedGCInfo)
                {
                    var xmlOutputName = CacheFiles.FindFile(FilePath, ".gcStats." + processId.ToString() + ".xml");
                    var csvFile = CacheFiles.FindFile(FilePath, ".gcStats." + processId.ToString() + ".csv");
                    if (!File.Exists(xmlOutputName) || File.GetLastWriteTimeUtc(xmlOutputName) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(xmlOutputName) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                    {
                        using (var writer = File.CreateText(xmlOutputName))
                            gcProc.ToXml(writer, "");
                    }

                    // TODO FIX NOW Need a way of viewing it.  
                    var viewer = Command.FindOnPath("xmlView");
                    if (viewer == null)
                        viewer = "notepad";

                    Command.Run(viewer + " " + Command.Quote(xmlOutputName),
                        new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite).AddNoThrow());
                    return viewer + " launched on " + xmlOutputName;
                }
            }
            return "Unknown command " + command;
        }

        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter writer, string fileName, TextWriter log)
        {
            var source = dataFile.Events.GetSource();
            m_gcStats = GCProcess.Collect(source, (float)dataFile.SampleProfileInterval.TotalMilliseconds, null, null, false, dataFile);
            m_gcStats.ToHtml(writer, fileName, "GCStats", null, true);
        }

        ProcessLookup<GCProcess> m_gcStats;
    }

    public class PerfViewJitStats : PerfViewHtmlReport
    {
        public PerfViewJitStats(PerfViewFile dataFile) : base(dataFile, "JITStats") { }
        protected override string DoCommand(string command, StatusBar worker)
        {
            if (command.StartsWith("excel/"))
            {
                var rest = command.Substring(6);
                var processId = int.Parse(rest);
                JitProcess jitProc;
                if (m_jitStats.TryGetByID(processId, out jitProc))
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".jitStats." + processId.ToString() + ".csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                        jitProc.ToCsv(csvFile);
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV " + csvFile;
                }
            }
            else if (command.StartsWith("excelInlining/"))
            {
                var rest = command.Substring(14);
                var processId = int.Parse(rest);
                JitProcess jitProc;
                if (m_jitStats.TryGetByID(processId, out jitProc))
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".jitInliningStats." + processId.ToString() + ".csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                        jitProc.ToInliningCsv(csvFile);
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV " + csvFile;
                }
            }
            else if (command.StartsWith("excelBackgroundDiag/"))
            {
                var rest = command.Substring(20);
                var processId = int.Parse(rest);
                JitProcess jitProc;
                if (m_jitStats.TryGetByID(processId, out jitProc))
                {
                    var csvFile = CacheFiles.FindFile(FilePath, ".BGjitStats." + processId.ToString() + ".csv");
                    if (!File.Exists(csvFile) || File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(FilePath) ||
                        File.GetLastWriteTimeUtc(csvFile) < File.GetLastWriteTimeUtc(SupportFiles.ExePath))
                        jitProc.BackgroundDiagCsv(csvFile);
                    Command.Run(Command.Quote(csvFile), new CommandOptions().AddStart().AddTimeout(CommandOptions.Infinite));
                    System.Threading.Thread.Sleep(500);     // Give it time to start a bit.  
                    return "Opening CSV " + csvFile;
                }
            }

            return "Unknown command " + command;
        }

        protected override void WriteHtmlBody(TraceLog dataFile, TextWriter output, string fileName, TextWriter log)
        {
            m_jitStats = JitProcess.Collect(dataFile.Events.GetSource());
            m_jitStats.ToHtml(output, fileName, "JITStats", null, true);
        }

        ProcessLookup<JitProcess> m_jitStats;
    }

    /// <summary>
    /// Represents all the heap snapshots in the trace
    /// </summary>
    public class PerfViewHeapSnapshots : PerfViewTreeItem
    {
        public PerfViewHeapSnapshots(ETLPerfViewData file)
        {
            Name = "GC Heap Snapshots";
            DataFile = file;
        }

        public virtual string Title { get { return Name + " for " + DataFile.Title; } }
        public ETLPerfViewData DataFile { get; private set; }
        public override string FilePath { get { return DataFile.FilePath; } }

        /// <summary>
        /// Open the file (This might be expensive (but maybe not).  This should populate the Children property 
        /// too.  
        /// </summary>
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter)
        {
            if (m_Children == null)
            {
                var newChildren = new List<PerfViewTreeItem>();
                worker.StartWork("Searching for heap dumps in " + Name, delegate ()
                {
                    var traceLog = DataFile.GetTraceLog(worker.LogWriter);
                    var source = traceLog.Events.GetSource();
                    var jsHeapParser = new JSDumpHeapTraceEventParser(source);


                    // For .NET, we are looking for a Gen 2 GC Start that is induced that has GCBulkNodes after it.   
                    var lastGCStartsRelMSec = new Dictionary<int, double>();

                    source.Clr.GCStart += delegate (Microsoft.Diagnostics.Tracing.Parsers.Clr.GCStartTraceData data)
                    {
                        // Look for induced GCs.  and remember their when it happened.    
                        if (data.Depth == 2 && data.Reason == GCReason.Induced)
                            lastGCStartsRelMSec[data.ProcessID] = data.TimeStampRelativeMSec;
                    };
                    source.Clr.GCBulkNode += delegate (GCBulkNodeTraceData data)
                    {
                        double lastGCStartRelMSec;
                        if (lastGCStartsRelMSec.TryGetValue(data.ProcessID, out lastGCStartRelMSec))
                        {
                            var processName = "";
                            var process = data.Process();
                            if (process != null)
                                processName = process.Name;
                            newChildren.Add(new PerfViewHeapSnapshot(DataFile, data.ProcessID, processName, lastGCStartRelMSec, ".NET"));

                            lastGCStartsRelMSec.Remove(data.ProcessID);     // Remove it since so we ignore the rest of the node events.  
                        }
                    };

                    jsHeapParser.JSDumpHeapEnvelopeStart += delegate (SettingsTraceData data)
                    {
                        var processName = "";
                        var process = data.Process();
                        if (process != null)
                            processName = process.Name;
                        newChildren.Add(new PerfViewHeapSnapshot(DataFile, data.ProcessID, processName, data.TimeStampRelativeMSec, "JS"));
                    };
                    source.Process();

                    worker.EndWork(delegate ()
                    {
                        m_Children = newChildren;
                        FirePropertyChanged("Children");
                        if (doAfter != null)
                            doAfter();
                    });
                });
            }
            if (doAfter != null)
                doAfter();
        }
        /// <summary>
        /// Close the file
        /// </summary>
        public override void Close() { }

        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FolderOpenBitmapImage"] as ImageSource; } }
    }

    /// <summary>
    /// Represents a single heap snapshot in a ETL file (currently only JScript).  
    /// </summary>
    class PerfViewHeapSnapshot : HeapDumpPerfViewFile
    {
        /// <summary>
        /// snapshotKinds should be .NET or JS
        /// </summary>
        public PerfViewHeapSnapshot(ETLPerfViewData file, int processId, string processName, double timeRelativeMSec, string snapshotKind)
        {
            m_snapshotKind = snapshotKind;
            m_timeRelativeMSec = timeRelativeMSec;
            m_filePath = file.FilePath;
            Kind = snapshotKind;
            m_processId = processId;
            Name = snapshotKind + " Heap Snapshot " + processName + "(" + processId + ") at " + timeRelativeMSec.ToString("n3") + " MSec";
        }
        public override string HelpAnchor { get { return "JSHeapSnapshot"; } }
        public string Kind { get; private set; }

        internal string m_snapshotKind;
        internal double m_timeRelativeMSec;
        internal int m_processId;
    };

    public class PerfViewEventSource : PerfViewTreeItem
    {
        public PerfViewEventSource(PerfViewFile dataFile)
        {
            DataFile = dataFile;
            Name = "Events";
        }

        public PerfViewEventSource(ETWEventSource source)
        {
        }
        public virtual string Title { get { return "Events " + DataFile.Title; } }
        public PerfViewFile DataFile { get; private set; }
        public EventWindow Viewer { get; internal set; }
        public virtual EventSource GetEventSource()
        {
            Debug.Assert(m_eventSource != null, "Open must be called first");
            if (m_needClone)
                return m_eventSource.Clone();
            m_needClone = true;
            return m_eventSource;
        }
        public override string FilePath { get { return DataFile.FilePath; } }
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter)
        {
            if (Viewer == null || !DataFile.IsUpToDate)
            {
                worker.StartWork("Opening " + Name, delegate ()
                {
                    if (m_eventSource == null || !DataFile.IsUpToDate)
                        m_eventSource = DataFile.OpenEventSourceImpl(worker.LogWriter);
                    worker.EndWork(delegate ()
                    {
                        if (m_eventSource == null)
                            throw new ApplicationException("Not a file type that supports the EventView.");
                        Viewer = new EventWindow(parentWindow, this);
                        Viewer.Show();
                        if (doAfter != null)
                            doAfter();
                    });
                });
            }
            else
            {
                Viewer.Focus();
                if (doAfter != null)
                    doAfter();
            }
        }
        public override void Close() { }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["EventSourceBitmapImage"] as ImageSource; } }

        #region private
        internal EventSource m_eventSource;     // TODO internal is a hack
        private bool m_needClone;           // After giving this out the first time, we need to clone it 
        #endregion
    }

    public class PerfViewStackSource : PerfViewTreeItem
    {
        public PerfViewStackSource(PerfViewFile dataFile, string sourceName)
        {
            DataFile = dataFile;
            SourceName = sourceName;
            if (sourceName.EndsWith(" TaskTree"))   // Special case, call it 'TaskTree' to make it clearer that it is not a call stack
                Name = SourceName;
            else
                Name = SourceName + " Stacks";
        }
        public PerfViewFile DataFile { get; private set; }
        public string SourceName { get; private set; }
        public StackWindow Viewer { get; internal set; }
        public override string HelpAnchor { get { return SourceName.Replace(" ", "") + "Stacks"; } }
        public virtual string Title { get { return SourceName + " Stacks " + DataFile.Title; } }
        public virtual StackSource GetStackSource(TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity)
        {
            if (m_StackSource != null && DataFile.IsUpToDate && startRelativeMSec == 0 && endRelativeMSec == double.PositiveInfinity)
                return m_StackSource;

            StackSource ret = DataFile.OpenStackSourceImpl(SourceName, log, startRelativeMSec, endRelativeMSec);
            if (ret == null)
                ret = DataFile.OpenStackSourceImpl(log);
            if (ret == null)
                throw new ApplicationException("Not a file type that supports the StackView.");

            if (startRelativeMSec == 0 && endRelativeMSec == double.PositiveInfinity)
                m_StackSource = ret;
            return ret;
        }

        // TODO not clear I want this method 
        protected virtual StackSource OpenStackSource(
            string streamName, TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity, Predicate<TraceEvent> predicate = null)
        {
            return DataFile.OpenStackSourceImpl(streamName, log, startRelativeMSec, endRelativeMSec, predicate);
        }
        // TODO not clear I want this method (client could do it).  
        protected virtual void SetProcessFilter(string incPat)
        {
            Viewer.IncludeRegExTextBox.Text = incPat;
        }
        protected internal virtual StackSource OpenStackSourceImpl(TextWriter log) { return DataFile.OpenStackSourceImpl(log); }
        public override string FilePath { get { return DataFile.FilePath; } }
        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            if (Viewer == null || !DataFile.IsUpToDate)
            {
                worker.StartWork("Opening " + Name, delegate ()
                {
                    if (m_StackSource == null || !DataFile.IsUpToDate)
                    {
                        // Compute the stack events
                        m_StackSource = OpenStackSource(SourceName, worker.LogWriter);
                        if (m_StackSource == null)
                            m_StackSource = OpenStackSourceImpl(worker.LogWriter);
                        if (m_StackSource == null)
                            throw new ApplicationException("Not a file type that supports the StackView.");
                    }

                    // Get the process summary if needed. 
                    List<IProcess> processes = null;
                    // TODO Using the source name here is a bit of hack.  Heap Allocations, however are already filtered to a process. 
                    if (DataFile.SupportsProcesses && SourceName != "Net OS Heap Alloc")
                    {
                        worker.Log("[Computing the processes involved in the trace.]");
                        processes = DataFile.GetProcesses(worker.LogWriter);
                    }

                    worker.EndWork(delegate ()
                    {
                        // This is the action that happens either after select process or after the stacks are computed.  
                        Action<List<IProcess>> launchViewer = delegate (List<IProcess> selectedProcesses)
                        {
                            Viewer = new StackWindow(parentWindow, this);
                            ConfigureStackWindow(Viewer);
                            Viewer.Show();

                            List<int> processIDs = null;
                            if (selectedProcesses != null && selectedProcesses.Count != 0)
                            {
                                processIDs = new List<int>();
                                string incPat = "";
                                foreach (var process in selectedProcesses)
                                {
                                    if (incPat.Length != 0)
                                        incPat += "|";
                                    incPat += "Process% " + process.Name + " (" + process.ProcessID + ")";
                                    processIDs.Add(process.ProcessID);
                                }
                                SetProcessFilter(incPat);
                            }

                            Viewer.StatusBar.StartWork("Looking up high importance PDBs that are locally cached", delegate
                            {
                                // TODO This is probably a hack that it is here.  
                                var etlDataFile = DataFile as ETLPerfViewData;
                                TraceLog traceLog = null;
                                if (etlDataFile != null)
                                {
                                    var moduleFiles = CommandProcessor.GetInterestingModuleFiles(etlDataFile, 5.0, Viewer.StatusBar.LogWriter, processIDs);
                                    traceLog = etlDataFile.GetTraceLog(Viewer.StatusBar.LogWriter);
                                    using (var reader = etlDataFile.GetSymbolReader(Viewer.StatusBar.LogWriter,
                                        SymbolReaderOptions.CacheOnly | SymbolReaderOptions.NoNGenSymbolCreation))
                                    {
                                        foreach (var moduleFile in moduleFiles)
                                        {
                                            try
                                            {
                                                // TODO FIX NOW don't throw exceptions, 
                                                Viewer.StatusBar.Log("[Quick lookup of " + moduleFile.Name + "]");
                                                traceLog.CodeAddresses.LookupSymbolsForModule(reader, moduleFile);
                                            }
                                            catch (ApplicationException ex)
                                            {
                                                Viewer.StatusBar.Log("[Error looking up " + moduleFile.FilePath + "]\r\n    " + ex.Message);
                                            }
                                        }
                                    }
                                }
                                Viewer.StatusBar.EndWork(delegate
                                {
                                    // Catch the error if you don't merge and move to a new machine.  
                                    if (traceLog != null && !traceLog.CurrentMachineIsCollectionMachine() && !traceLog.HasPdbInfo)
                                        MessageBox.Show(parentWindow,
                                            "Warning!   This file was not merged and was moved from the collection\r\n" +
                                            "machine.  This means the data is incomplete and symbolic name resolution\r\n" +
                                            "will NOT work.  The recommended fix is use the perfview (not windows OS)\r\n" +
                                            "zip command.  Right click on the file in the main view and select ZIP.\r\n" +
                                            "\r\n" +
                                            "See merging and zipping in the users guide for more information.",
                                            "Data not merged before leaving the machine!");

                                    Viewer.SetStackSource(m_StackSource, delegate ()
                                    {
                                        worker.Log("Opening Viewer.");
                                        if (WarnAboutBrokenStacks(Viewer, Viewer.StatusBar.LogWriter))
                                        {
                                            // TODO, WPF leaves blank regions after the dialog box is dismissed.  
                                            // Force a redraw by changing the size.  This should not be needed.   
                                            var width = Viewer.Width;
                                            Viewer.Width = width - 1;
                                            Viewer.Width = width;
                                        }
                                        FirstAction(Viewer);
                                        if (doAfter != null)
                                            doAfter();
                                    });
                                });
                            });
                        };

                        if (processes != null && !SkipSelectProcess)
                        {
                            if (DataFile.InitiallyIncludedProcesses == null)
                            {
                                m_SelectProcess = new SelectProcess(processes, new TimeSpan(1, 0, 0), delegate (List<IProcess> selectedProcesses)
                                {
                                    launchViewer(selectedProcesses);
                                }, hasAllProc: true);
                                m_SelectProcess.Show();
                            }
                            else
                            {
                                launchViewer(processes.Where(p => DataFile.InitiallyIncludedProcesses
                                    .Any(iip => string.Equals(p.Name, iip, StringComparison.OrdinalIgnoreCase)))
                                    .ToList());
                            }
                        }
                        else
                            launchViewer(null);
                    });
                });
            }
            else
            {
                Viewer.Focus();
                if (doAfter != null)
                    doAfter();
            }
        }

        public override void Close() { }
        protected internal virtual void ConfigureStackWindow(StackWindow stackWindow)
        {
            DataFile.ConfigureStackWindow(SourceName, stackWindow);
        }
        protected internal virtual void FirstAction(StackWindow stackWindow)
        {
            DataFile.FirstAction(stackWindow);
        }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["StackSourceBitmapImage"] as ImageSource; } }

        // If set, we don't show the process selection dialog.  
        public bool SkipSelectProcess;
        #region private
        internal void ViewClosing(StackWindow viewer)
        {
            Viewer = null;
            DataFile.StackSourceClosing(this);
        }

        private bool WarnAboutBrokenStacks(Window parentWindow, TextWriter log)
        {
            if (!m_WarnedAboutBrokenStacks)
            {
                m_WarnedAboutBrokenStacks = true;
                float brokenPercent = Viewer.CallTree.Root.GetBrokenStackCount() * 100 / Viewer.CallTree.Root.InclusiveCount;
                if (brokenPercent > 0)
                {
                    bool is64bit = false;
                    foreach (var child in Viewer.CallTree.Root.Callees)
                    {
                        // if there is any process we can't determine is 64 bit, then we assume it might be.  
                        if (!child.Name.StartsWith("Process32 "))
                            is64bit = true;
                    }
                    return WarnAboutBrokenStacks(parentWindow, brokenPercent, is64bit, log);
                }
            }
            return false;
        }
        private static bool WarnAboutBrokenStacks(Window parentWindow, float brokenPercent, bool is64Bit, TextWriter log)
        {
            if (brokenPercent > 1)
                log.WriteLine("Finished Agregating stacks.  (" + brokenPercent.ToString("f1") + "% Broken Stacks)");
            if (brokenPercent > 10)
            {
                if (is64Bit)
                {
                    MessageBox.Show(parentWindow, "Warning: There are " + brokenPercent.ToString("f1") +
                        "% stacks that are broken, analysis is suspect." + "\r\n" +
                        "This is likely due the current inability of the OS stackwalker to walk 64 bit\r\n" +
                        "code that is dynamically (JIT) generated.\r\n\r\n" +
                        "This can be worked around by either by NGENing the EXE,\r\n" +
                        "forcing the EXE to run as a 32 bit app, profiling on Windows 8\r\n" +
                        "or avoiding any top-down analysis.\r\n\r\n" +
                        "Use the troubleshooting link at the top of the view for more information.\r\n",
                        "Broken Stacks");
                }
                else
                    MessageBox.Show(parentWindow, "Warning: There are " + brokenPercent.ToString("f1") + "% stacks that are broken\r\n" +
                        "Top down analysis is suspect, however bottom up approaches are still valid.\r\n\r\n" +
                        "Use the troubleshooting link at the top of the view for more information.\r\n",
                        "Broken Stacks");
                return true;
            }
            return false;
        }

        internal StackSource m_StackSource;
        internal SelectProcess m_SelectProcess;
        private bool m_WarnedAboutBrokenStacks;
        #endregion
    }

    class DiffPerfViewData : PerfViewStackSource
    {
        public DiffPerfViewData(PerfViewStackSource data, PerfViewStackSource baseline)
            : base(data.DataFile, data.SourceName)
        {
            m_baseline = baseline;
            m_data = data;
            Name = string.Format("Diff {0} baseline {1}", data.Name, baseline.Name);
        }

        public override string Title
        {
            get
            {
                // TODO do better. 
                return Name;
            }
        }
        public override StackSource GetStackSource(TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity)
        {
            return InternStackSource.Diff(m_data.GetStackSource(log, startRelativeMSec, endRelativeMSec), m_baseline.GetStackSource(log, startRelativeMSec, endRelativeMSec));
        }

        #region private
        PerfViewStackSource m_data;
        PerfViewStackSource m_baseline;
        #endregion
    }

    /// <summary>
    /// These are the data Templates that PerfView understands.  
    /// </summary>
    class CSVPerfViewData : PerfViewFile
    {
        public override string FormatName { get { return "XPERF CSV"; } }
        public override string[] FileExtensions { get { return new string[] { ".csvz", ".etl.csv" }; } }

        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            m_csvReader = new CSVReader.CSVReader(FilePath);
            m_Children = new List<PerfViewTreeItem>();

            m_Children.Add(new PerfViewEventSource(this));
            foreach (var stackEventName in m_csvReader.StackEventNames)
                m_Children.Add(new PerfViewStackSource(this, stackEventName));
            return null;
        }
        public override void Close()
        {
            if (m_csvReader != null)
            {
                m_csvReader.Dispose();
                m_csvReader = null;
            }
            base.Close();
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.FoldPercentTextBox.Text = stackWindow.GetDefaultFoldPercentage();
            ConfigureAsEtwStackWindow(stackWindow, stackSourceName == "SampledProfile");
        }
        public override bool SupportsProcesses { get { return true; } }
        protected internal override StackSource OpenStackSourceImpl(
            string streamName, TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity, Predicate<TraceEvent> predicate = null)
        {
            // TODO: predicate not used
            return m_csvReader.StackSamples(streamName, startRelativeMSec, endRelativeMSec);
        }
        protected internal override EventSource OpenEventSourceImpl(TextWriter log)
        {
            return m_csvReader.GetEventSource();
        }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FileBitmapImage"] as ImageSource; } }

        #region private
        private CSVReader.CSVReader m_csvReader;
        #endregion
    }

    public partial class ETLPerfViewData : PerfViewFile
    {
        public override string FormatName { get { return "ETW"; } }
        public override string[] FileExtensions { get { return new string[] { ".etl", ".etlx", ".etl.zip", ".vspx" }; } }

        protected internal override EventSource OpenEventSourceImpl(TextWriter log)
        {
            var traceLog = GetTraceLog(log);
            return new ETWEventSource(traceLog);
        }
        protected internal override StackSource OpenStackSourceImpl(string streamName, TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity, Predicate<TraceEvent> predicate = null)
        {
            var eventLog = GetTraceLog(log);
            if (streamName == "CPU")
            {
                return eventLog.CPUStacks(null, App.CommandLineArgs.ShowUnknownAddresses, predicate);
            }

            // var stackSource = new InternTraceEventStackSource(eventLog);
            var stackSource = new MutableTraceEventStackSource(eventLog);

            stackSource.ShowUnknownAddresses = App.CommandLineArgs.ShowUnknownAddresses;

            TraceEvents events = eventLog.Events;
            if (!streamName.Contains("TaskTree") && !streamName.Contains("Tasks)"))
            {
                if (predicate != null)
                    events = events.Filter(predicate);
            }
            else
                startRelativeMSec = 0;    // These require activity computers and thus need earlier events.   

            if (startRelativeMSec != 0 || endRelativeMSec != double.PositiveInfinity)
                events = events.FilterByTime(startRelativeMSec, endRelativeMSec);

            var eventSource = events.GetSource();
            var sample = new StackSourceSample(stackSource);

            if (streamName == "Thread Time (with Tasks)")
            {
                return eventLog.ThreadTimeWithTasksStacks();
            }
            else if (streamName == "Thread Time (with ReadyThread)")
            {
                return eventLog.ThreadTimeWithReadyThreadStacks();
            }
            else if (streamName.StartsWith("ASP.NET Thread Time"))
            {
                if (streamName == "ASP.NET Thread Time (with Tasks)")
                    return eventLog.ThreadTimeWithTasksAspNetStacks();
                else
                    return eventLog.ThreadTimeAspNetStacks();
            }
            else if (streamName == "Thread Time (with StartStop Activities)")
            {
                var startStopSource = new MutableTraceEventStackSource(eventLog);

                var computer = new ThreadTimeStackComputer(eventLog, App.GetSymbolReader(eventLog.FilePath));
                computer.UseTasks = true;
                computer.GroupByStartStopActivity = true;
                computer.ExcludeReadyThread = true;
                computer.GenerateThreadTimeStacks(startStopSource);

                return startStopSource;
            }
            else if (streamName == "Thread Time")
            {
                return eventLog.ThreadTimeStacks();
            }
            else if (streamName == "Processes / Files / Registry")
            {
                return GetProcessFileRegistryStackSource(eventSource, log);
            }
            else if (streamName == "GC Heap Alloc Ignore Free")
            {
                var gcHeapSimulators = new GCHeapSimulators(eventLog, eventSource, stackSource, log);
                gcHeapSimulators.OnNewGCHeapSimulator = delegate (GCHeapSimulator newHeap)
                {
                    newHeap.OnObjectCreate += delegate (Address objAddress, GCHeapSimulatorObject objInfo)
                    {
                        sample.Metric = objInfo.RepresentativeSize;
                        sample.Count = objInfo.RepresentativeSize / objInfo.Size;                                               // We guess a count from the size.  
                        sample.TimeRelativeMSec = objInfo.AllocationTimeRelativeMSec;
                        sample.StackIndex = stackSource.Interner.CallStackIntern(objInfo.ClassFrame, objInfo.AllocStack);        // Add the type as a pseudo frame.  
                        stackSource.AddSample(sample);
                        return true;
                    };
                };
                eventSource.Process();
                stackSource.DoneAddingSamples();
            }
            else if (streamName.StartsWith("GC Heap Net Mem"))
            {
                var gcHeapSimulators = new GCHeapSimulators(eventLog, eventSource, stackSource, log);
                if (streamName == "GC Heap Net Mem (Coarse Sampling)")
                {
                    gcHeapSimulators.UseOnlyAllocTicks = true;
                    m_extraTopStats = "Sampled only 100K bytes";
                }

                gcHeapSimulators.OnNewGCHeapSimulator = delegate (GCHeapSimulator newHeap)
                {
                    newHeap.OnObjectCreate += delegate (Address objAddress, GCHeapSimulatorObject objInfo)
                    {
                        sample.Metric = objInfo.RepresentativeSize;
                        sample.Count = objInfo.RepresentativeSize / objInfo.Size;                                                // We guess a count from the size.  
                        sample.TimeRelativeMSec = objInfo.AllocationTimeRelativeMSec;
                        sample.StackIndex = stackSource.Interner.CallStackIntern(objInfo.ClassFrame, objInfo.AllocStack);        // Add the type as a pseudo frame.  
                        stackSource.AddSample(sample);
                        return true;
                    };
                    newHeap.OnObjectDestroy += delegate (double time, int gen, Address objAddress, GCHeapSimulatorObject objInfo)
                    {
                        sample.Metric = -objInfo.RepresentativeSize;
                        sample.Count = -(objInfo.RepresentativeSize / objInfo.Size);                                            // We guess a count from the size.  
                        sample.TimeRelativeMSec = time;
                        sample.StackIndex = stackSource.Interner.CallStackIntern(objInfo.ClassFrame, objInfo.AllocStack);       // We remove the same stack we added at alloc.  
                        stackSource.AddSample(sample);
                    };
                };
                eventSource.Process();
                stackSource.DoneAddingSamples();
            }
            else if (streamName == "Gen 2 Object Deaths")
            {
                var gcHeapSimulators = new GCHeapSimulators(eventLog, eventSource, stackSource, log);
                gcHeapSimulators.OnNewGCHeapSimulator = delegate (GCHeapSimulator newHeap)
                {
                    newHeap.OnObjectDestroy += delegate (double time, int gen, Address objAddress, GCHeapSimulatorObject objInfo)
                    {
                        if (2 <= gen)
                        {
                            sample.Metric = objInfo.RepresentativeSize;
                            sample.Count = (objInfo.RepresentativeSize / objInfo.Size);                                         // We guess a count from the size.  
                            sample.TimeRelativeMSec = objInfo.AllocationTimeRelativeMSec;
                            sample.StackIndex = stackSource.Interner.CallStackIntern(objInfo.ClassFrame, objInfo.AllocStack);
                            stackSource.AddSample(sample);
                        }
                    };
                };
                eventSource.Process();
                stackSource.DoneAddingSamples();
            }
            else if (streamName == "GC Heap Alloc Ignore Free (Coarse Sampling)")
            {
                TypeNameSymbolResolver typeNameSymbolResolver = new TypeNameSymbolResolver(FilePath, log);

                bool seenBadAllocTick = false;

                eventSource.Clr.GCAllocationTick += delegate (GCAllocationTickTraceData data)
                {
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                    var stackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);

                    var typeName = data.TypeName;
                    if (string.IsNullOrEmpty(typeName))
                    {
                        // Attempt to resolve the type name.
                        TraceLoadedModule module = data.Process().LoadedModules.GetModuleContainingAddress(data.TypeID, data.TimeStampRelativeMSec);
                        if (module != null)
                        {
                            // Resolve the type name.
                            typeName = typeNameSymbolResolver.ResolveTypeName((int)(data.TypeID - module.ModuleFile.ImageBase), module.ModuleFile, TypeNameSymbolResolver.TypeNameOptions.StripModuleName);
                        }
                    }

                    if (typeName != null && typeName.Length > 0)
                    {
                        var nodeIndex = stackSource.Interner.FrameIntern("Type " + typeName);
                        stackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackIndex);
                    }

                    sample.Metric = data.GetAllocAmount(ref seenBadAllocTick);

                    if (data.AllocationKind == GCAllocationKind.Large)
                    {

                        var nodeIndex = stackSource.Interner.FrameIntern("LargeObject");
                        stackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackIndex);
                    }

                    sample.StackIndex = stackIndex;
                    stackSource.AddSample(sample);
                };
                eventSource.Process();
                m_extraTopStats = "Sampled only 100K bytes";
            }
            else if (streamName == "Exceptions")
            {
                eventSource.Clr.ExceptionStart += delegate (ExceptionTraceData data)
                {
                    sample.Metric = 1;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                    // Create a call stack that ends with the 'throw'
                    var nodeName = "Throw(" + data.ExceptionType + ") " + data.ExceptionMessage;
                    var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                    sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                    stackSource.AddSample(sample);
                };

                eventSource.Kernel.MemoryAccessViolation += delegate (MemoryPageFaultTraceData data)
                {
                    sample.Metric = 1;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                    // Create a call stack that ends with the 'throw'
                    var nodeName = "AccessViolation(ADDR=" + data.VirtualAddress.ToString("x") + ")";
                    var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                    sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                    stackSource.AddSample(sample);
                };

                eventSource.Process();
            }

            else if (streamName == "Pinning At GC Time")
            {
                // Wire up the GC heap simulations.  
                GCHeapSimulators gcHeapSimulators = new GCHeapSimulators(eventLog, eventSource, stackSource, log);

                // Keep track of the current GC per process 
                var curGCGen = new int[eventLog.Processes.Count];
                var curGCIndex = new int[eventLog.Processes.Count];
                eventSource.Clr.GCStart += delegate (Microsoft.Diagnostics.Tracing.Parsers.Clr.GCStartTraceData data)
                {
                    var process = data.Process();
                    if (process == null)
                        return;
                    curGCGen[(int)process.ProcessIndex] = data.Depth;
                    curGCIndex[(int)process.ProcessIndex] = data.Count;
                };

                // Keep track of the live Pinning handles per process.  
                var allLiveHandles = new Dictionary<Address, GCHandleInfo>[eventLog.Processes.Count];
                Action<SetGCHandleTraceData> onSetHandle = delegate (SetGCHandleTraceData data)
                {
                    if (!(data.Kind == GCHandleKind.AsyncPinned || data.Kind == GCHandleKind.Pinned))
                        return;

                    var process = data.Process();
                    if (process == null)
                        return;
                    var liveHandles = allLiveHandles[(int)process.ProcessIndex];
                    if (liveHandles == null)
                        allLiveHandles[(int)process.ProcessIndex] = liveHandles = new Dictionary<Address, GCHandleInfo>();

                    GCHandleInfo info;
                    var handle = data.HandleID;
                    if (!liveHandles.TryGetValue(handle, out info))
                    {
                        liveHandles[handle] = info = new GCHandleInfo();
                        info.PinStartTimeRelativeMSec = data.TimeStampRelativeMSec;
                        info.ObjectAddress = data.ObjectID;
                        info.IsAsync = (data.Kind == GCHandleKind.AsyncPinned || data.Kind == GCHandleKind.DependendAsyncPinned);
                        info.GCGen = (byte)data.Generation;
                        info.PinStack = stackSource.GetCallStack(data.CallStackIndex(), data);

                        // watch this object as it GCs happen  (but frankly it should not move).  
                        gcHeapSimulators[process].TrackObject(info.ObjectAddress);
                    }
                };
                var clrPrivate = new ClrPrivateTraceEventParser(eventSource);
                clrPrivate.GCSetGCHandle += onSetHandle;
                eventSource.Clr.GCSetGCHandle += onSetHandle;

                Action<DestroyGCHandleTraceData> onDestroyHandle = delegate (DestroyGCHandleTraceData data)
                {
                    var process = data.Process();
                    if (process == null)
                        return;
                    var liveHandles = allLiveHandles[(int)process.ProcessIndex];
                    if (liveHandles == null)
                        allLiveHandles[(int)process.ProcessIndex] = liveHandles = new Dictionary<Address, GCHandleInfo>();

                    GCHandleInfo info;
                    var handle = data.HandleID;
                    if (liveHandles.TryGetValue(handle, out info))
                        liveHandles.Remove(handle);
                };
                clrPrivate.GCDestroyGCHandle += onDestroyHandle;
                eventSource.Clr.GCDestoryGCHandle += onDestroyHandle;

#if false 
                var cacheAllocated = new Dictionary<Address, bool>();
                Action<TraceEvent> onPinnableCacheAllocate = delegate(TraceEvent data) 
                {
                    var objectId = (Address) data.PayloadByName("objectId");
                    cacheAllocated[objectId] = true;
                };
                eventSource.Dynamic.AddCallbackForProviderEvent("AllocateBuffer", "Microsoft-DotNETRuntime-PinnableBufferCache", onPinnableCacheAllocate);
                eventSource.Dynamic.AddCallbackForProviderEvent("AllocateBuffer", "Microsoft-DotNETRuntime-PinnableBufferCache-Mscorlib", onPinnableCacheAllocate); 

                Action<PinPlugAtGCTimeTraceData> plugAtGCTime = delegate(PinPlugAtGCTimeTraceData data)
                {
                };
                clrPrivate.GCPinPlugAtGCTime += plugAtGCTime;
                eventSource.Clr.GCPinObjectAtGCTime += plugAtGCTime;
#endif
                // ThreadStacks maps locations in memory of the thread stack to and maps it to a thread.  
                var threadStacks = new Dictionary<Address, TraceThread>[eventLog.Processes.Count];

                // This per-thread information is used solely as a heuristic backstop to try to guess what
                // the Pinned handles are when we don't have other information.   We can remove it. 
                var lastHandleInfoForThreads = new PerThreadGCHandleInfo[eventLog.Threads.Count];

                // The main event, we have pinning that is happening at GC time.  
                Action<PinObjectAtGCTimeTraceData> objectAtGCTime = delegate (PinObjectAtGCTimeTraceData data)
                {
                    var thread = data.Thread();
                    if (thread == null)
                        return;
                    var process = thread.Process;
                    var liveHandles = allLiveHandles[(int)process.ProcessIndex];
                    if (liveHandles == null)
                        allLiveHandles[(int)process.ProcessIndex] = liveHandles = new Dictionary<Address, GCHandleInfo>();

                    string pinKind = "UnknownPinned";
                    double pinStartTimeRelativeMSec = 0;
                    StackSourceCallStackIndex pinStack = StackSourceCallStackIndex.Invalid;
                    StackSourceCallStackIndex allocStack = StackSourceCallStackIndex.Invalid;
                    int gcGen = curGCGen[(int)process.ProcessIndex];
                    int gcIndex = curGCIndex[(int)process.ProcessIndex];

                    GCHandleInfo info;
                    if (liveHandles.TryGetValue(data.HandleID, out info))
                    {
                        pinStack = info.PinStack;
                        if (pinStack != StackSourceCallStackIndex.Invalid)
                        {
                            pinStartTimeRelativeMSec = info.PinStartTimeRelativeMSec;
                            pinKind = "HandlePinned";
                            gcGen = info.GCGen;
                        }
                        else if (data.ObjectID == info.ObjectAddress)
                            pinStartTimeRelativeMSec = info.PinStartTimeRelativeMSec;
                        else
                        {
                            info.PinStartTimeRelativeMSec = data.TimeStampRelativeMSec;     // Restart trying to guess how long this lives
                            info.ObjectAddress = data.ObjectID;
                        }
                    }
                    else
                    {
                        liveHandles[data.HandleID] = info = new GCHandleInfo();
                        info.ObjectAddress = data.ObjectID;
                        info.PinStartTimeRelativeMSec = data.TimeStampRelativeMSec;         // We guess the pinning started at this GC.  
                    }

                    // This is heuristic logic to determine if the pin handles are async or not. 
                    // Basically async handles are themselves pinned and then point at pinned things.  Thus
                    // if you see handles that point near other handles that is likely an async handle. 
                    // TODO I think we can remove this, because we no longer pin the async handle.  
                    if (pinStack == StackSourceCallStackIndex.Invalid)
                    {
                        var lastHandleInfo = lastHandleInfoForThreads[(int)thread.ThreadIndex];
                        if (lastHandleInfo == null)
                            lastHandleInfoForThreads[(int)thread.ThreadIndex] = lastHandleInfo = new PerThreadGCHandleInfo();

                        // If we see a handle that 
                        if (data.HandleID - lastHandleInfo.LikelyAsyncHandleTable1 < 128)
                        {
                            pinKind = "LikelyAsyncPinned";
                            lastHandleInfo.LikelyAsyncHandleTable1 = data.HandleID;
                        }
                        else if (data.HandleID - lastHandleInfo.LikelyAsyncHandleTable2 < 128)
                        {
                            // This is here for the async array of buffers case.   
                            pinKind = "LikelyAsyncPinned";
                            lastHandleInfo.LikelyAsyncHandleTable2 = lastHandleInfo.LikelyAsyncHandleTable1;
                            lastHandleInfo.LikelyAsyncHandleTable1 = data.HandleID;
                        }
                        if (data.HandleID - lastHandleInfo.LastObject < 128)
                        {
                            pinKind = "LikelyAsyncDependentPinned";
                            lastHandleInfo.LikelyAsyncHandleTable2 = lastHandleInfo.LikelyAsyncHandleTable1;
                            lastHandleInfo.LikelyAsyncHandleTable1 = lastHandleInfo.LastHandle;
                        }

                        // Remember our values for heuristics we use to determine if it is an async 
                        lastHandleInfo.LastHandle = data.HandleID;
                        lastHandleInfo.LastObject = data.ObjectID;
                    }

                    var objectInfo = gcHeapSimulators[process].GetObjectInfo(data.ObjectID);
                    if (objectInfo != null)
                    {
                        allocStack = objectInfo.AllocStack;
                        if ((allocStack != StackSourceCallStackIndex.Invalid) && (objectInfo.ClassFrame != StackSourceFrameIndex.Invalid))
                        {
                            if (512 <= objectInfo.Size)
                            {
                                var frameName = stackSource.GetFrameName(objectInfo.ClassFrame, false);

                                var size = 1024;
                                while (size < objectInfo.Size)
                                    size = size * 2;

                                frameName += " <= " + (size / 1024).ToString() + "K";
                                allocStack = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(frameName), allocStack);
                            }
                            else
                                allocStack = stackSource.Interner.CallStackIntern(objectInfo.ClassFrame, allocStack);
                        }
                    }

                    // If we did not get pinning information, see if it is a stack pin
                    if (pinStack == StackSourceCallStackIndex.Invalid)
                    {
                        const Address allocQuantum = 0x10000 - 1;   // 64K, must be a power of 2.  

                        var threadStack = threadStacks[(int)process.ProcessIndex];
                        if (threadStack == null)
                        {
                            threadStacks[(int)process.ProcessIndex] = threadStack = new Dictionary<Address, TraceThread>();

                            foreach (var procThread in process.Threads)
                            {
                                // Round up to the next 64K boundary
                                var loc = (procThread.UserStackBase + allocQuantum) & ~allocQuantum;
                                // We assume thread stacks are .5 meg (8 * 64K)   Growing down.  
                                for (int i = 0; i < 8; i++)
                                {
                                    threadStack[loc] = procThread;
                                    loc -= (allocQuantum + 1);
                                }
                            }
                        }
                        Address roundUp = (data.HandleID + allocQuantum) & ~allocQuantum;
                        TraceThread stackThread;
                        if (threadStack.TryGetValue(roundUp, out stackThread) && stackThread.StartTimeRelativeMSec <= data.TimeStampRelativeMSec && data.TimeStampRelativeMSec < stackThread.EndTimeRelativeMSec)
                        {
                            pinKind = "StackPinned";
                            pinStack = stackSource.GetCallStackForThread(stackThread);
                        }
                    }

                    /*****  OK we now have all the information we collected, create the sample.  *****/
                    sample.StackIndex = StackSourceCallStackIndex.Invalid;

                    // Choose the stack to use 
                    if (allocStack != StackSourceCallStackIndex.Invalid)
                    {
                        sample.StackIndex = allocStack;
                        sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Allocation Location"), sample.StackIndex);
                    }
                    else if (pinStack != StackSourceCallStackIndex.Invalid)
                    {
                        sample.StackIndex = pinStack;
                        sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Pinning Location"), sample.StackIndex);
                    }
                    else
                    {
                        var gcThread = data.Thread();
                        if (gcThread == null)
                            return;             // TODO WARN

                        sample.StackIndex = stackSource.GetCallStackForThread(gcThread);
                        sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("GC Location"), sample.StackIndex);
                    }

                    // Add GC Number
                    sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("GC_NUM " + gcIndex), sample.StackIndex);

                    // Duration of the pin. 
                    var pinDuration = "UNKNOWN";
                    if (pinStartTimeRelativeMSec != 0)
                    {
                        var pinDurationMSec = data.TimeStampRelativeMSec - pinStartTimeRelativeMSec;
                        var roundedDuration = Math.Pow(10.0, Math.Ceiling(Math.Log10(pinDurationMSec)));
                        pinDuration = "<= " + roundedDuration.ToString("n");
                    }
                    var pinDurationInfo = "PINNED_FOR " + pinDuration + " msec";
                    sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(pinDurationInfo), sample.StackIndex);

                    // Add the Pin Kind;
                    sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(pinKind), sample.StackIndex);

                    // Add the type and size 
                    var typeName = data.TypeName;
                    if (data.ObjectSize > 0)
                        sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Type " + typeName + " Size: 0x" + data.ObjectSize.ToString("x")), sample.StackIndex);

                    // Add the generation.
                    sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Generation " + gcGen), sample.StackIndex);

                    // _sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Handle 0x" + data.HandleID.ToString("x") +  " Object 0x" + data.ObjectID.ToString("x")), _sample.StackIndex);

                    // We now have the stack, fill in the rest of the _sample and add it to the stack source.  
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.Metric = 1;
                    stackSource.AddSample(sample);
                };
                eventSource.Clr.GCPinObjectAtGCTime += objectAtGCTime;
                clrPrivate.GCPinObjectAtGCTime += objectAtGCTime;         // TODO FIX NOW REMOVE AFTER PRIVATE IS GONE

                eventSource.Process();
                stackSource.DoneAddingSamples();
            }
            else if (streamName == "Pinning")
            {
                var clrPrivate = new ClrPrivateTraceEventParser(eventSource);
                var liveHandles = new Dictionary<long, GCHandleInfo>();
                int maxLiveHandles = 0;
                double maxLiveHandleRelativeMSec = 0;

                Action<SetGCHandleTraceData> onSetHandle = delegate (SetGCHandleTraceData data)
                {
                    if (!(data.Kind == GCHandleKind.AsyncPinned || data.Kind == GCHandleKind.Pinned))
                        return;

                    GCHandleInfo info;
                    var handle = (long)data.HandleID;
                    if (!liveHandles.TryGetValue(handle, out info))
                    {
                        liveHandles[handle] = info = new GCHandleInfo();
                        if (liveHandles.Count > maxLiveHandles)
                        {
                            maxLiveHandles = liveHandles.Count;
                            maxLiveHandleRelativeMSec = data.TimeStampRelativeMSec;
                        }
                        info.PinStartTimeRelativeMSec = data.TimeStampRelativeMSec;
                        info.ObjectAddress = data.ObjectID;

                        // TODO deal with nulling out. 
                        string nodeName = (data.Kind == GCHandleKind.Pinned) ? "SinglePinned" : "AsyncPinned";
                        StackSourceFrameIndex frameIndex = stackSource.Interner.FrameIntern(nodeName);
                        StackSourceCallStackIndex callStackIndex = stackSource.Interner.CallStackIntern(frameIndex, stackSource.GetCallStack(data.CallStackIndex(), data));

                        // Add the generation.
                        nodeName = "Generation " + data.Generation;
                        frameIndex = stackSource.Interner.FrameIntern(nodeName);
                        info.PinStack = stackSource.Interner.CallStackIntern(frameIndex, callStackIndex);
                    }
                };
                clrPrivate.GCSetGCHandle += onSetHandle;
                eventSource.Clr.GCSetGCHandle += onSetHandle;

                Action<DestroyGCHandleTraceData> onDestroyHandle = delegate (DestroyGCHandleTraceData data)
                {
                    GCHandleInfo info;
                    var handle = (long)data.HandleID;
                    if (liveHandles.TryGetValue(handle, out info))
                    {
                        LogGCHandleLifetime(stackSource, sample, info, data.TimeStampRelativeMSec, log);
                        liveHandles.Remove(handle);
                    }
                };
                clrPrivate.GCDestroyGCHandle += onDestroyHandle;
                eventSource.Clr.GCDestoryGCHandle += onDestroyHandle;

                eventSource.Process();
                // Pick up any handles that were never destroyed.  
                foreach (var info in liveHandles.Values)
                    LogGCHandleLifetime(stackSource, sample, info, eventLog.SessionDuration.TotalMilliseconds, log);

                stackSource.DoneAddingSamples();
                log.WriteLine("The maximum number of live pinning handles is {0} at {1:n3} Msec ", maxLiveHandles, maxLiveHandleRelativeMSec);
            }

            else if (streamName == "Heap Snapshot Pinning")
            {
                GCPinnedObjectAnalyzer pinnedObjectAnalyzer = new GCPinnedObjectAnalyzer(this.FilePath, eventLog, stackSource, sample, log);
                pinnedObjectAnalyzer.Execute(GCPinnedObjectViewType.PinnedHandles);
            }
            else if (streamName == "Heap Snapshot Pinned Object Allocation")
            {
                GCPinnedObjectAnalyzer pinnedObjectAnalyzer = new GCPinnedObjectAnalyzer(this.FilePath, eventLog, stackSource, sample, log);
                pinnedObjectAnalyzer.Execute(GCPinnedObjectViewType.PinnedObjectAllocations);
            }
            else if (streamName == "CCW Ref Count")
            {
                // TODO use the callback model.  We seem to have an issue getting the names however. 
                foreach (var data in events.ByEventType<CCWRefCountChangeTraceData>())
                {
                    sample.Metric = 1;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    var stackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);

                    var operation = data.Operation;
                    if (operation.StartsWith("Release", StringComparison.OrdinalIgnoreCase))
                        sample.Metric = -1;

                    var ccwRefKindName = "CCW " + operation;
                    var ccwRefKindIndex = stackSource.Interner.FrameIntern(ccwRefKindName);
                    stackIndex = stackSource.Interner.CallStackIntern(ccwRefKindIndex, stackIndex);

                    var ccwRefCountName = "CCW NewRefCnt " + data.NewRefCount.ToString();
                    var ccwRefCountIndex = stackSource.Interner.FrameIntern(ccwRefCountName);
                    stackIndex = stackSource.Interner.CallStackIntern(ccwRefCountIndex, stackIndex);

                    var ccwInstanceName = "CCW Instance 0x" + data.COMInterfacePointer.ToString("x");
                    var ccwInstanceIndex = stackSource.Interner.FrameIntern(ccwInstanceName);
                    stackIndex = stackSource.Interner.CallStackIntern(ccwInstanceIndex, stackIndex);

                    var ccwTypeName = "CCW Type " + data.NameSpace + "." + data.ClassName;
                    var ccwTypeIndex = stackSource.Interner.FrameIntern(ccwTypeName);
                    stackIndex = stackSource.Interner.CallStackIntern(ccwTypeIndex, stackIndex);

                    sample.StackIndex = stackIndex;
                    stackSource.AddSample(sample);
                }
            }
            else if (streamName == "Windows Handle Ref Count")
            {
                var allocationsStacks = new Dictionary<long, StackSourceCallStackIndex>(200);

                Action<string, Address, int, int, TraceEvent> onHandleEvent = delegate (string handleTypeName, Address objectInstance, int handleInstance, int handleProcess, TraceEvent data)
                {
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.StackIndex = StackSourceCallStackIndex.Invalid;

                    sample.Metric = 1;
                    // Closes use the call stack of the allocation site if possible (since that is more helpful)  
                    if (data.Opcode == (TraceEventOpcode)33)       // CloseHandle
                    {
                        sample.Metric = -1;

                        long key = (((long)handleProcess) << 32) + handleInstance;
                        StackSourceCallStackIndex stackIndex;
                        if (allocationsStacks.TryGetValue(key, out stackIndex))
                            sample.StackIndex = stackIndex;
                        // TODO should we keep track of the ref count and remove the entry when it drops past zero?  
                    }

                    // If this not a close() (Or if we could not find a stack for the close() make up a call stack from the event.  
                    if (sample.StackIndex == StackSourceCallStackIndex.Invalid)
                    {
                        StackSourceCallStackIndex stackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);

                        // We want all stacks to be int he process where the handle exists.  But this not always the case
                        // If that happened abandon the stack and make up a pseudo stack that lets you know that happened. 
                        if (handleProcess != data.ProcessID)
                        {
                            stackIndex = StackSourceCallStackIndex.Invalid;
                            TraceProcess process = eventLog.Processes.GetProcess(handleProcess, data.TimeStampRelativeMSec);
                            if (process != null)
                                stackIndex = stackSource.GetCallStackForProcess(process);

                            var markerIndex = stackSource.Interner.FrameIntern("Handle Allocated Out of Process");
                            stackIndex = stackSource.Interner.CallStackIntern(markerIndex, stackIndex);
                        }

                        var nameIndex = stackSource.Interner.FrameIntern(data.EventName);
                        stackIndex = stackSource.Interner.CallStackIntern(nameIndex, stackIndex);

                        var instanceName = "Handle Instance " + handleInstance.ToString("n0") + " (0x" + handleInstance.ToString("x") + ")";
                        var instanceIndex = stackSource.Interner.FrameIntern(instanceName);
                        stackIndex = stackSource.Interner.CallStackIntern(instanceIndex, stackIndex);

                        var handleTypeIndex = stackSource.Interner.FrameIntern("Handle Type " + handleTypeName);
                        stackIndex = stackSource.Interner.CallStackIntern(handleTypeIndex, stackIndex);

                        //var objectName = "Object Instance 0x" + objectInstance.ToString("x");
                        //var objectIndex = stackSource.Interner.FrameIntern(objectName);
                        //stackIndex = stackSource.Interner.CallStackIntern(objectIndex, stackIndex);

                        sample.StackIndex = stackIndex;

                        long key = (((long)handleProcess) << 32) + handleInstance;
                        allocationsStacks[key] = stackIndex;
                    }

                    stackSource.AddSample(sample);
                };

                eventSource.Kernel.AddCallbackForEvents<ObjectHandleTraceData>(data => onHandleEvent(data.ObjectTypeName, data.Object, data.Handle, data.ProcessID, data));
                eventSource.Kernel.AddCallbackForEvents<ObjectDuplicateHandleTraceData>(data => onHandleEvent(data.ObjectTypeName, data.Object, data.TargetHandle, data.TargetProcessID, data));
                eventSource.Process();
            }
            else if (streamName.StartsWith("Any"))
            {
                ActivityComputer activityComputer = null;
                StartStopActivityComputer startStopComputer = null;
                bool isAnyTaskTree = (streamName == "Any TaskTree");
                bool isAnyWithTasks = (streamName == "Any Stacks (with Tasks)");
                bool isAnyWithStartStop = (streamName == "Any Stacks (with StartStop Activities)");          // These have the call stacks 
                bool isAnyStartStopTreeNoCallStack = (streamName == "Any StartStopTree");               // These have just the start-stop activities.  
                if (isAnyTaskTree || isAnyWithTasks || isAnyWithStartStop || isAnyStartStopTreeNoCallStack)
                {
                    activityComputer = new ActivityComputer(eventSource, GetSymbolReader(log));

                    // Log a pseudo-event that indicates when the activity dies
                    activityComputer.Stop += delegate (TraceActivity activity, TraceEvent data)
                    {
                        // TODO This is a clone of the logic below, factor it.  
                        TraceThread thread = data.Thread();
                        if (thread != null)
                            return;

                        StackSourceCallStackIndex stackIndex;
                        if (isAnyTaskTree)
                        {
                            // Compute the stack where frames using an activity Name as a frame name.
                            stackIndex = activityComputer.GetActivityStack(stackSource, activityComputer.GetCurrentActivity(thread));
                        }
                        else if (isAnyStartStopTreeNoCallStack)
                        {
                            stackIndex = startStopComputer.GetStartStopActivityStack(stackSource, startStopComputer.GetCurrentStartStopActivity(thread, data), thread.Process);
                        }
                        else
                        {
                            Func<TraceThread, StackSourceCallStackIndex> topFrames = null;
                            if (isAnyWithStartStop)
                                topFrames = delegate (TraceThread topThread) { return startStopComputer.GetCurrentStartStopActivityStack(stackSource, thread, topThread); };

                            // Use the call stack 
                            stackIndex = activityComputer.GetCallStack(stackSource, data, topFrames);
                        }

                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("ActivityStop " + activity.ToString()), stackIndex);
                        sample.StackIndex = stackIndex;
                        sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                        sample.Metric = 1;
                        stackSource.AddSample(sample);
                    };

                    if (isAnyWithStartStop || isAnyStartStopTreeNoCallStack)
                        startStopComputer = new StartStopActivityComputer(eventSource, activityComputer);
                }

                StackSourceFrameIndex blockingFrame = stackSource.Interner.FrameIntern("Event Kernel/Thread/BLOCKING CSwitch");
                StackSourceFrameIndex cswitchEventFrame = stackSource.Interner.FrameIntern("Event Windows Kernel/Thread/CSwitch");
                StackSourceFrameIndex readyThreadEventFrame = stackSource.Interner.FrameIntern("Event Windows Kernel/Dispatcher/ReadyThread");
                StackSourceFrameIndex sampledProfileFrame = stackSource.Interner.FrameIntern("Event Windows Kernel/PerfInfo/Sample");

                eventSource.AllEvents += delegate (TraceEvent data)
                {
                    // Get most of the stack (we support getting the normal call stack as well as the task stack.  
                    StackSourceCallStackIndex stackIndex;
                    if (activityComputer != null)
                    {
                        TraceThread thread = data.Thread();
                        if (thread == null)
                            return;

                        if (isAnyTaskTree)
                        {
                            // Compute the stack where frames using an activity Name as a frame name.
                            stackIndex = activityComputer.GetActivityStack(stackSource, activityComputer.GetCurrentActivity(thread));
                        }
                        else if (isAnyStartStopTreeNoCallStack)
                        {
                            stackIndex = startStopComputer.GetStartStopActivityStack(stackSource, startStopComputer.GetCurrentStartStopActivity(thread, data), thread.Process);
                        }
                        else
                        {
                            Func<TraceThread, StackSourceCallStackIndex> topFrames = null;
                            if (isAnyWithStartStop)
                                topFrames = delegate (TraceThread topThread) { return startStopComputer.GetCurrentStartStopActivityStack(stackSource, thread, topThread); };

                            // Use the call stack 
                            stackIndex = activityComputer.GetCallStack(stackSource, data, topFrames);
                        }
                    }
                    else
                    {
                        // Normal case, get the calls stack of frame names.  
                        var callStackIdx = data.CallStackIndex();
                        if (callStackIdx != CallStackIndex.Invalid)
                            stackIndex = stackSource.GetCallStack(callStackIdx, data);
                        else
                            stackIndex = StackSourceCallStackIndex.Invalid;
                    }

                    var asCSwitch = data as CSwitchTraceData;
                    if (asCSwitch != null)
                    {
                        if (activityComputer == null)  // Just a plain old any-stacks
                        {
                            var callStackIdx = asCSwitch.BlockingStack();
                            if (callStackIdx != CallStackIndex.Invalid)
                            {
                                // Make an entry for the blocking stacks as well.  
                                sample.StackIndex = stackSource.Interner.CallStackIntern(blockingFrame, stackSource.GetCallStack(callStackIdx, data));
                                sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                                sample.Metric = 1;
                                stackSource.AddSample(sample);
                            }
                        }

                        if (stackIndex != StackSourceCallStackIndex.Invalid)
                        {
                            stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData NewProcessName " + asCSwitch.NewProcessName), stackIndex);
                            stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData OldProcessName " + asCSwitch.OldProcessName), stackIndex);
                            stackIndex = stackSource.Interner.CallStackIntern(cswitchEventFrame, stackIndex);
                        }
                        goto ADD_SAMPLE;
                    }

                    if (stackIndex == StackSourceCallStackIndex.Invalid)
                        return;

                    var asSampledProfile = data as SampledProfileTraceData;
                    if (asSampledProfile != null)
                    {
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData Priority " + asSampledProfile.Priority), stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData Processor " + asSampledProfile.ProcessorNumber), stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(sampledProfileFrame, stackIndex);
                        goto ADD_SAMPLE;
                    }

                    var asReadyThread = data as DispatcherReadyThreadTraceData;
                    if (asReadyThread != null)
                    {
                        var awakenedName = "EventData Readied Thread " + asReadyThread.AwakenedThreadID +
                            " Proc " + asReadyThread.AwakenedProcessID;
                        var awakenedIndex = stackSource.Interner.FrameIntern(awakenedName);
                        stackIndex = stackSource.Interner.CallStackIntern(awakenedIndex, stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(readyThreadEventFrame, stackIndex);
                        goto ADD_SAMPLE;
                    }

                    // TODO FIX NOW remove for debugging activity stuff.  
#if false 
                    var activityId = data.ActivityID;
                    if (activityId != Guid.Empty && ActivityComputer.IsActivityPath(activityId))
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("ActivityPath " + ActivityComputer.ActivityPathString(activityId)), stackIndex);
#endif
                    var asObjectAllocated = data as ObjectAllocatedArgs;
                    if (asObjectAllocated != null)
                    {
                        var size = "EventData Size 0x" + asObjectAllocated.Size.ToString("x");
                        var sizeIndex = stackSource.Interner.FrameIntern(size);
                        stackIndex = stackSource.Interner.CallStackIntern(sizeIndex, stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    var asSampleObjectAllocated = data as GCSampledObjectAllocationTraceData;
                    if (asSampleObjectAllocated != null)
                    {
                        var size = "EventData Size 0x" + asSampleObjectAllocated.TotalSizeForTypeSample.ToString("x");
                        var sizeIndex = stackSource.Interner.FrameIntern(size);
                        stackIndex = stackSource.Interner.CallStackIntern(sizeIndex, stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    var asSetGCHandle = data as SetGCHandleTraceData;
                    if (asSetGCHandle != null)
                    {
                        var handleName = "EventData GCHandleKind " + asSetGCHandle.Kind.ToString();
                        var handleIndex = stackSource.Interner.FrameIntern(handleName);
                        stackIndex = stackSource.Interner.CallStackIntern(handleIndex, stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    var asPageAccess = data as MemoryPageAccessTraceData;
                    if (asPageAccess != null)
                    {
                        var pageKind = asPageAccess.PageKind;
                        sample.Metric = 4;      // Convenience since these are 4K pages 
                        if (pageKind == PageKind.ProcessPrivate)
                        {
                            var address = asPageAccess.VirtualAddress;
                            var process = data.Process();
                            if (process != null)
                            {
                                var module = process.LoadedModules.GetModuleContainingAddress(address, asPageAccess.TimeStampRelativeMSec);
                                if (module != null)
                                {
                                    stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData Image  " + module.ModuleFile.FilePath), stackIndex);
                                    stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("EventData CopyOnWrite"), stackIndex);
                                }
                            }
                        }
                        else
                        {
                            string fileName = asPageAccess.FileName;
                            if (fileName.Length > 0)
                            {
                                stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(pageKind.ToString() + " " + fileName), stackIndex);
                                pageKind = PageKind.File;
                            }
                        }
                        var kindIdx = stackSource.Interner.FrameIntern(pageKind.ToString());
                        stackIndex = stackSource.Interner.CallStackIntern(kindIdx, stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    var asPMCCounter = data as PMCCounterProfTraceData;
                    if (asPMCCounter != null)
                    {
                        var source = "EventData ProfileSourceID " + asPMCCounter.ProfileSource;
                        var sourceIndex = stackSource.Interner.FrameIntern(source);
                        stackIndex = stackSource.Interner.CallStackIntern(sourceIndex, stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    var asFileCreate = data as FileIOCreateTraceData;
                    if (asFileCreate != null)
                    {
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("CreateOptions: " + asFileCreate.CreateOptions), stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("FileAttributes: " + asFileCreate.FileAttributes), stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("ShareAccess: " + asFileCreate.ShareAccess), stackIndex);
                        // stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("CreateDispostion: " + asFileCreate.CreateDispostion), stackIndex);
                        stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("FileName: " + asFileCreate.FileName), stackIndex);
                        goto ADD_EVENT_FRAME;
                    }

                    // Tack on additional info about the event. 
                    var fieldNames = data.PayloadNames;
                    for (int i = 0; i < fieldNames.Length; i++)
                    {
                        var fieldName = fieldNames[i];
                        if (0 <= fieldName.IndexOf("Name", StringComparison.OrdinalIgnoreCase) ||
                            fieldName == "OpenPath" || fieldName == "Url" || fieldName == "Uri" || fieldName == "ConnectionId" ||
                            fieldName == "ExceptionType" || 0 <= fieldName.IndexOf("Message", StringComparison.OrdinalIgnoreCase))
                        {
                            var value = data.PayloadString(i);
                            var fieldNodeName = "EventData " + fieldName + " " + value;
                            var fieldNodeIndex = stackSource.Interner.FrameIntern(fieldNodeName);
                            stackIndex = stackSource.Interner.CallStackIntern(fieldNodeIndex, stackIndex);
                        }
                    }

                    ADD_EVENT_FRAME:
                    // Tack on event name 
                    var eventNodeName = "Event " + data.ProviderName + "/" + data.EventName;
                    stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(eventNodeName), stackIndex);
                    ADD_SAMPLE:
                    sample.StackIndex = stackIndex;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.Metric = 1;
                    stackSource.AddSample(sample);
                };
                eventSource.Process();
            }
            else if (streamName == "Managed Load")
            {
                eventSource.Clr.LoaderModuleLoad += delegate (ModuleLoadUnloadTraceData data)
                {
                    sample.Metric = 1;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                    // Create a call stack that ends with 'Disk READ <fileName> (<fileDirectory>)'
                    var nodeName = "Load " + data.ModuleILPath;
                    var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                    sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                    stackSource.AddSample(sample);
                };
                eventSource.Process();
            }
            else if (streamName == "Disk I/O")
            {
                var diskStartStack = new Dictionary<Address, StackSourceCallStackIndex>(50);
                // On a per-disk basis remember when the last Disk I/O completed.  
                var lastDiskEndMSec = new GrowableArray<double>(4);

                eventSource.Kernel.AddCallbackForEvents<DiskIOInitTraceData>(delegate (DiskIOInitTraceData data)
                {
                    diskStartStack[data.Irp] = stackSource.GetCallStack(data.CallStackIndex(), data);
                });

                eventSource.Kernel.AddCallbackForEvents<DiskIOTraceData>(delegate (DiskIOTraceData data)
                {
                    StackSourceCallStackIndex stackIdx;
                    if (diskStartStack.TryGetValue(data.Irp, out stackIdx))
                        diskStartStack.Remove(data.Irp);
                    else
                        stackIdx = StackSourceCallStackIndex.Invalid;

                    var diskNumber = data.DiskNumber;
                    if (diskNumber >= lastDiskEndMSec.Count)
                        lastDiskEndMSec.Count = diskNumber + 1;

                    // Create a call stack that ends with 'Disk READ <fileName> (<fileDirectory>)'
                    var filePath = data.FileName;
                    if (filePath.Length == 0)
                        filePath = "UNKNOWN";

                    var nodeName = "I/O Size 0x" + data.TransferSize.ToString("x");
                    var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                    stackIdx = stackSource.Interner.CallStackIntern(nodeIndex, stackIdx);

                    nodeName = string.Format("Disk {0} DiskNum({1}) {2} ({3})", data.OpcodeName, diskNumber,
                        GetFileName(filePath), GetDirectoryName(filePath));

                    // The time it took actually using the disk is the smaller of either
                    // The elapsed time (because there were no other entries in the disk queue)
                    // OR the time since the last I/O completed (since that is when this one will start using the disk.
                    var elapsedMSec = data.ElapsedTimeMSec;
                    double serviceTimeMSec = elapsedMSec;
                    double durationSinceLastIOMSec = data.TimeStampRelativeMSec - lastDiskEndMSec[diskNumber];
                    lastDiskEndMSec[diskNumber] = elapsedMSec;
                    if (durationSinceLastIOMSec < serviceTimeMSec)
                    {
                        serviceTimeMSec = durationSinceLastIOMSec;
                        // There is queuing delay, indicate this by adding a sample that represents the queueing delay. 

                        var queueStackIdx = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Time in Disk Queue " + diskNumber), stackIdx);
                        sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(nodeName), queueStackIdx);
                        sample.Metric = (float)(elapsedMSec - serviceTimeMSec);
                        sample.TimeRelativeMSec = data.TimeStampRelativeMSec - elapsedMSec;
                        stackSource.AddSample(sample);
                    }

                    stackIdx = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Service Time Disk " + diskNumber), stackIdx);
                    sample.StackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern(nodeName), stackIdx);
                    sample.Metric = (float)serviceTimeMSec;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec - serviceTimeMSec;
                    stackSource.AddSample(sample);
                });
                eventSource.Process();
                m_extraTopStats = " Metric is MSec";
            }
            else if (streamName == "Server Request CPU")
            {
                ServerRequestScenarioConfiguration scenarioConfiguration = new ServerRequestScenarioConfiguration(eventLog);
                ComputingResourceStateMachine stateMachine = new ComputingResourceStateMachine(stackSource, scenarioConfiguration, ComputingResourceViewType.CPU);
                stateMachine.Execute();
            }
            else if (streamName == "Server Request Thread Time")
            {
                ServerRequestScenarioConfiguration scenarioConfiguration = new ServerRequestScenarioConfiguration(eventLog);
                ComputingResourceStateMachine stateMachine = new ComputingResourceStateMachine(stackSource, scenarioConfiguration, ComputingResourceViewType.ThreadTime);
                stateMachine.Execute();
            }
            else if (streamName == "Server Request Managed Allocation")
            {
                ServerRequestScenarioConfiguration scenarioConfiguration = new ServerRequestScenarioConfiguration(eventLog);
                ComputingResourceStateMachine stateMachine = new ComputingResourceStateMachine(stackSource, scenarioConfiguration, ComputingResourceViewType.Allocations);
                stateMachine.Execute();
            }
            else if (streamName == "Execution Tracing")
            {
                TraceLogEventSource source = eventLog.Events.GetSource();

                Action<TraceEvent> tracingCallback = delegate (TraceEvent data)
                {
                    string assemblyName = (string)data.PayloadByName("assembly");
                    string typeName = (string)data.PayloadByName("type");
                    string methodName = (string)data.PayloadByName("method");

                    string frameName = string.Format("{0}!{1}.{2}", assemblyName, typeName, methodName);

                    StackSourceCallStackIndex callStackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);
                    StackSourceFrameIndex nodeIndex = stackSource.Interner.FrameIntern(frameName);
                    callStackIndex = stackSource.Interner.CallStackIntern(nodeIndex, callStackIndex);

                    sample.Count = 1;
                    sample.Metric = 1;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.StackIndex = callStackIndex;

                    stackSource.AddSample(sample);
                };

                source.Dynamic.AddCallbackForProviderEvent("MethodCallLogger", "MethodEntry", tracingCallback);

                source.Process();
            }
            else if (streamName == "File I/O")
            {
                eventSource.Kernel.AddCallbackForEvents<FileIOReadWriteTraceData>(delegate (FileIOReadWriteTraceData data)
                {
                    sample.Metric = (float)data.IoSize;
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                    StackSourceCallStackIndex stackIdx = stackSource.GetCallStack(data.CallStackIndex(), data);

                    // Create a call stack that ends with 'Disk READ <fileName> (<fileDirectory>)'
                    var filePath = data.FileName;
                    if (filePath.Length == 0)
                        filePath = "UNKNOWN";

                    var nodeName = string.Format("File {0}: {1} ({2})", data.OpcodeName,
                        GetFileName(filePath), GetDirectoryName(filePath));
                    var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                    stackIdx = stackSource.Interner.CallStackIntern(nodeIndex, stackIdx);

                    sample.StackIndex = stackIdx;
                    stackSource.AddSample(sample);
                });
                eventSource.Process();
            }
            else if (streamName == "Image Load")
            {
                var loadedImages = new Dictionary<Address, StackSourceCallStackIndex>(100);
                Action<ImageLoadTraceData> imageLoadUnload = delegate (ImageLoadTraceData data)
                {
                    // TODO this is not really correct, it assumes process IDs < 64K and images bases don't use lower bits
                    // but it is true 
                    Address imageKey = data.ImageBase + (Address)data.ProcessID;

                    sample.Metric = data.ImageSize;
                    if (data.Opcode == TraceEventOpcode.Stop)
                    {
                        sample.StackIndex = StackSourceCallStackIndex.Invalid;
                        StackSourceCallStackIndex allocIdx;
                        if (loadedImages.TryGetValue(imageKey, out allocIdx))
                            sample.StackIndex = allocIdx;
                        sample.Metric = -sample.Metric;
                    }
                    else
                    {
                        // Create a call stack that ends with 'Load <fileName> (<fileDirectory>)'
                        var fileName = data.FileName;
                        var nodeName = "Image Load " + GetFileName(fileName) + " (" + GetDirectoryName(fileName) + ")";
                        var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                        sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                        loadedImages[imageKey] = sample.StackIndex;
                    }
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    stackSource.AddSample(sample);
                };
                eventSource.Kernel.ImageLoad += imageLoadUnload;
                eventSource.Kernel.ImageUnload += imageLoadUnload;
                eventSource.Process();
            }
            else if (streamName == "Net Virtual Alloc")
            {
                var droppedEvents = 0;
                var memStates = new MemState[eventLog.Processes.Count];
                eventSource.Kernel.AddCallbackForEvents<VirtualAllocTraceData>(delegate (VirtualAllocTraceData data)
                {
                    bool isAlloc = false;
                    if ((data.Flags & (
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT |
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_DECOMMIT |
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_RELEASE)) != 0)
                    {
                        // Can't use data.Process() because some of the virtual allocs occur in the process that started the
                        // process and occur before the process start event, which is what Process() uses to find it. 
                        // TODO this code assumes that process launch is within 1 second and process IDs are not aggressively reused. 
                        var processWhereMemoryAllocated = data.Log().Processes.GetProcess(data.ProcessID, data.TimeStampRelativeMSec + 1000);
                        if (processWhereMemoryAllocated == null)
                        {
                            droppedEvents++;
                            return;
                        }

                        var processIndex = processWhereMemoryAllocated.ProcessIndex;
                        var memState = memStates[(int)processIndex];
                        if (memState == null)
                            memState = memStates[(int)processIndex] = new MemState();

                        // Commit and decommit not both on together.  
                        Debug.Assert((data.Flags &
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT | VirtualAllocTraceData.VirtualAllocFlags.MEM_DECOMMIT)) !=
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT | VirtualAllocTraceData.VirtualAllocFlags.MEM_DECOMMIT));

                        var stackIndex = StackSourceCallStackIndex.Invalid;
                        if ((data.Flags & VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT) != 0)
                        {
                            isAlloc = true;
                            // Some of the early allocations are actually by the process that starts this process.  Don't use their stacks 
                            // But do count them.  
                            var processIDAllocatingMemory = processWhereMemoryAllocated.ProcessID;  // This is not right, but it sets the condition properly below 
                            var thread = data.Thread();
                            if (thread != null)
                                processIDAllocatingMemory = thread.Process.ProcessID;

                            if (data.TimeStampRelativeMSec >= processWhereMemoryAllocated.StartTimeRelativeMsec && processIDAllocatingMemory == processWhereMemoryAllocated.ProcessID)
                                stackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);
                            else
                            {
                                stackIndex = stackSource.GetCallStackForProcess(processWhereMemoryAllocated);
                                stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Allocated In Parent Process"), stackIndex);
                            }
                        }
                        memState.Update(data.BaseAddr, data.Length, isAlloc, stackIndex,
                            delegate (long metric, StackSourceCallStackIndex allocStack)
                            {
                                Debug.Assert(allocStack != StackSourceCallStackIndex.Invalid);
                                Debug.Assert(metric != 0);                                                  // They should trim this already.  
                                sample.Metric = metric;
                                sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                                sample.StackIndex = allocStack;
                                stackSource.AddSample(sample);
                                // Debug.WriteLine("Sample Proc {0,12} Time {1,8:f3} Length 0x{2:x} Metric 0x{3:x} Stack {4,8} Cum {5,8}", process.Name, sample.TimeRelativeMSec, data.Length, (int) sample.Metric, (int)sample.StackIndex, memState.TotalMem);
                            });
                    }
                });
                eventSource.Process();
                if (droppedEvents != 0)
                    log.WriteLine("WARNING: {0} events were dropped because their process could not be determined.", droppedEvents);
            }
            else if (streamName == "Net Virtual Reserve")
            {
                // Mapped file (which includes image loads) logic. 
                var mappedImages = new Dictionary<Address, StackSourceCallStackIndex>(100);
                Action<MapFileTraceData> mapUnmapFile = delegate (MapFileTraceData data)
                {
                    sample.Metric = data.ViewSize;
                    // If it is a UnMapFile or MapFileDCStop event
                    if (data.Opcode == (TraceEventOpcode)38)
                    {
                        Debug.Assert(data.OpcodeName == "UnmapFile");
                        sample.StackIndex = StackSourceCallStackIndex.Invalid;
                        StackSourceCallStackIndex allocIdx;
                        if (mappedImages.TryGetValue(data.FileKey, out allocIdx))
                        {
                            sample.StackIndex = allocIdx;
                            mappedImages.Remove(data.FileKey);
                        }
                        sample.Metric = -sample.Metric;
                    }
                    else
                    {
                        Debug.Assert(data.OpcodeName == "MapFile" || data.OpcodeName == "MapFileDCStart");
                        // Create a call stack that ends with 'MapFile <fileName> (<fileDirectory>)'
                        var nodeName = "MapFile";
                        var fileName = data.FileName;
                        if (fileName.Length > 0)
                            nodeName = nodeName + " " + GetFileName(fileName) + " (" + GetDirectoryName(fileName) + ")";
                        var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
                        sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                        mappedImages[data.FileKey] = sample.StackIndex;
                    }
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    stackSource.AddSample(sample);
                };
                eventSource.Kernel.FileIOMapFile += mapUnmapFile;
                eventSource.Kernel.FileIOUnmapFile += mapUnmapFile;
                eventSource.Kernel.FileIOMapFileDCStart += mapUnmapFile;

                // Virtual Alloc logic
                var droppedEvents = 0;
                var memStates = new MemState[eventLog.Processes.Count];
                var virtualReserverFrame = stackSource.Interner.FrameIntern("VirtualReserve");
                eventSource.Kernel.AddCallbackForEvents<VirtualAllocTraceData>(delegate (VirtualAllocTraceData data)
                {
                    bool isAlloc = false;
                    if ((data.Flags & (
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT |
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_RESERVE |
                        VirtualAllocTraceData.VirtualAllocFlags.MEM_RELEASE)) != 0)
                    {
                        // Can't use data.Process() because some of the virtual allocs occur in the process that started the
                        // process and occur before the process start event, which is what Process() uses to find it. 
                        // TODO this code assumes that process launch is within 1 second and process IDs are not aggressively reused. 
                        var processWhereMemoryAllocated = data.Log().Processes.GetProcess(data.ProcessID, data.TimeStampRelativeMSec + 1000);
                        if (processWhereMemoryAllocated == null)
                        {
                            droppedEvents++;
                            return;
                        }

                        var processIndex = processWhereMemoryAllocated.ProcessIndex;
                        var memState = memStates[(int)processIndex];
                        if (memState == null)
                            memState = memStates[(int)processIndex] = new MemState();

                        // Commit and decommit not both on together.  
                        Debug.Assert((data.Flags &
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT | VirtualAllocTraceData.VirtualAllocFlags.MEM_DECOMMIT)) !=
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT | VirtualAllocTraceData.VirtualAllocFlags.MEM_DECOMMIT));
                        // Reserve and release not both on together.
                        Debug.Assert((data.Flags &
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_RESERVE | VirtualAllocTraceData.VirtualAllocFlags.MEM_RELEASE)) !=
                            (VirtualAllocTraceData.VirtualAllocFlags.MEM_RESERVE | VirtualAllocTraceData.VirtualAllocFlags.MEM_RELEASE));

                        // You allocate by committing or reserving.  We have already filtered out decommits which have no effect on reservation.  
                        // Thus the only memRelease is the only one that frees.  
                        var stackIndex = StackSourceCallStackIndex.Invalid;
                        if ((data.Flags & (VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT | VirtualAllocTraceData.VirtualAllocFlags.MEM_RESERVE)) != 0)
                        {
                            isAlloc = true;
                            // Some of the early allocations are actually by the process that starts this process.  Don't use their stacks 
                            // But do count them.  
                            var processIDAllocatingMemory = processWhereMemoryAllocated.ProcessID;  // This is not right, but it sets the condition properly below 
                            var thread = data.Thread();
                            if (thread != null)
                                processIDAllocatingMemory = thread.Process.ProcessID;

                            if (data.TimeStampRelativeMSec >= processWhereMemoryAllocated.StartTimeRelativeMsec && processIDAllocatingMemory == processWhereMemoryAllocated.ProcessID)
                                stackIndex = stackSource.GetCallStack(data.CallStackIndex(), data);
                            else
                            {
                                stackIndex = stackSource.GetCallStackForProcess(processWhereMemoryAllocated);
                                stackIndex = stackSource.Interner.CallStackIntern(stackSource.Interner.FrameIntern("Allocated In Parent Process"), stackIndex);
                            }
                            stackIndex = stackSource.Interner.CallStackIntern(virtualReserverFrame, stackIndex);
                        }
                        memState.Update(data.BaseAddr, data.Length, isAlloc, stackIndex,
                            delegate (long metric, StackSourceCallStackIndex allocStack)
                            {
                                Debug.Assert(allocStack != StackSourceCallStackIndex.Invalid);
                                Debug.Assert(metric != 0);                                                  // They should trim this already.  
                                sample.Metric = metric;
                                sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                                sample.StackIndex = allocStack;
                                stackSource.AddSample(sample);
                                // Debug.WriteLine("Sample Proc {0,12} Time {1,8:f3} Length 0x{2:x} Metric 0x{3:x} Stack {4,8} Cum {5,8}", process.Name, sample.TimeRelativeMSec, data.Length, (int) sample.Metric, (int)sample.StackIndex, memState.TotalMem);
                            });
                    }
                });
                eventSource.Process();
                if (droppedEvents != 0)
                    log.WriteLine("WARNING: {0} events were dropped because their process could not be determined.", droppedEvents);
            }
            else if (streamName == "Net OS Heap Alloc")
            {
                // We index by heap address and then within the heap we remember the allocation stack
                var heaps = new Dictionary<Address, Dictionary<Address, StackSourceSample>>();

                var heapParser = new HeapTraceProviderTraceEventParser(eventSource);
                Dictionary<Address, StackSourceSample> lastHeapAllocs = null;

                Address lastHeapHandle = 0;

                float peakMetric = 0;
                StackSourceSample peakSample = null;
                float cumMetric = 0;
                float sumCumMetric = 0;
                int cumCount = 0;

                heapParser.HeapTraceAlloc += delegate (HeapAllocTraceData data)
                {
                    var allocs = lastHeapAllocs;
                    if (data.HeapHandle != lastHeapHandle)
                        allocs = GetHeap(data.HeapHandle, heaps, ref lastHeapAllocs, ref lastHeapHandle);

                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.Metric = data.AllocSize;
                    var nodeIndex = stackSource.Interner.FrameIntern(GetAllocName((uint)data.AllocSize));
                    sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                    var addedSample = stackSource.AddSample(sample);
                    allocs[data.AllocAddress] = addedSample;

                    cumMetric += sample.Metric;
                    if (cumMetric > peakMetric)
                    {
                        peakMetric = cumMetric;
                        peakSample = addedSample;
                    }
                    sumCumMetric += cumMetric;
                    cumCount++;
                };

                heapParser.HeapTraceFree += delegate (HeapFreeTraceData data)
                {
                    var allocs = lastHeapAllocs;
                    if (data.HeapHandle != lastHeapHandle)
                        allocs = GetHeap(data.HeapHandle, heaps, ref lastHeapAllocs, ref lastHeapHandle);

                    StackSourceSample alloc;
                    if (allocs.TryGetValue(data.FreeAddress, out alloc))
                    {
                        cumMetric -= alloc.Metric;
                        sumCumMetric += cumMetric;
                        cumCount++;

                        allocs.Remove(data.FreeAddress);

                        Debug.Assert(alloc.Metric >= 0);
                        sample.Metric = -alloc.Metric;
                        sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                        sample.StackIndex = alloc.StackIndex;
                        stackSource.AddSample(sample);
                    }
                };

                heapParser.HeapTraceReAlloc += delegate (HeapReallocTraceData data)
                {
                    // Reallocs that actually move stuff will cause a Alloc and delete event
                    // so there is nothing to do for those events.  But when the address is
                    // the same we need to resize 
                    if (data.OldAllocAddress != data.NewAllocAddress)
                        return;

                    var allocs = lastHeapAllocs;
                    if (data.HeapHandle != lastHeapHandle)
                        allocs = GetHeap(data.HeapHandle, heaps, ref lastHeapAllocs, ref lastHeapHandle);

                    // This is a clone of the Free code 
                    StackSourceSample alloc;
                    if (allocs.TryGetValue(data.OldAllocAddress, out alloc))
                    {
                        cumMetric -= alloc.Metric;
                        sumCumMetric += cumMetric;
                        cumCount++;

                        allocs.Remove(data.OldAllocAddress);

                        Debug.Assert(alloc.Metric == data.OldAllocSize);
                        sample.Metric = -alloc.Metric;
                        sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                        sample.StackIndex = alloc.StackIndex;
                        stackSource.AddSample(sample);
                    }

                    // This is a clone of the Alloc code (sigh don't clone code)
                    sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                    sample.Metric = data.NewAllocSize;
                    var nodeIndex = stackSource.Interner.FrameIntern(GetAllocName((uint)data.NewAllocSize));
                    sample.StackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackSource.GetCallStack(data.CallStackIndex(), data));
                    var addedSample = stackSource.AddSample(sample);
                    allocs[data.NewAllocAddress] = addedSample;

                    cumMetric += sample.Metric;
                    if (cumMetric > peakMetric)
                    {
                        peakMetric = cumMetric;
                        peakSample = addedSample;
                    }
                    sumCumMetric += cumMetric;
                    cumCount++;
                };

                heapParser.HeapTraceDestroy += delegate (HeapTraceData data)
                {
                    // Heap is dieing, kill all objects in it.   
                    var allocs = lastHeapAllocs;
                    if (data.HeapHandle != lastHeapHandle)
                        allocs = GetHeap(data.HeapHandle, heaps, ref lastHeapAllocs, ref lastHeapHandle);

                    foreach (StackSourceSample alloc in allocs.Values)
                    {
                        // TODO this is a clone of the free code.  
                        cumMetric -= alloc.Metric;
                        sumCumMetric += cumMetric;
                        cumCount++;

                        Debug.Assert(alloc.Metric >= 0);
                        sample.Metric = -alloc.Metric;
                        sample.TimeRelativeMSec = data.TimeStampRelativeMSec;

                        sample.StackIndex = alloc.StackIndex;
                        stackSource.AddSample(sample);
                    }
                };
                eventSource.Process();

                var aveCumMetric = sumCumMetric / cumCount;
                log.WriteLine("Peak Heap Size: {0:n3} MB   Average Heap size: {1:n3} MB", peakMetric / 1000000.0F, aveCumMetric / 1000000.0F);
                if (peakSample != null)
                    log.WriteLine("Peak happens at {0:n3} Msec into the trace.", peakSample.TimeRelativeMSec);

                log.WriteLine("Trimming alloc-free pairs < 3 msec apart: Before we have {0:n1}K events now {1:n1}K events",
                    cumCount / 1000.0, stackSource.SampleIndexLimit / 1000.0);
                return stackSource;
            }
            else if (streamName == "Server GC")
            {
                GCProcess.Collect(eventSource, (float)eventLog.SampleProfileInterval.TotalMilliseconds, null, stackSource);
                return stackSource;
            }
            else throw new Exception("Unknown stream " + streamName);

            log.WriteLine("Produced {0:n3}K events", stackSource.SampleIndexLimit / 1000.0);
            stackSource.DoneAddingSamples();
            return stackSource;
        }

        #region private
        private static StackSource GetProcessFileRegistryStackSource(TraceLogEventSource eventSource, TextWriter log)
        {
            TraceLog traceLog = eventSource.TraceLog;

            // This maps a process Index to the stack that represents that process.  
            StackSourceCallStackIndex[] processStackCache = new StackSourceCallStackIndex[traceLog.Processes.Count];
            for (int i = 0; i < processStackCache.Length; i++)
                processStackCache[i] = StackSourceCallStackIndex.Invalid;

            var stackSource = new MutableTraceEventStackSource(eventSource.TraceLog);

            StackSourceSample sample = new StackSourceSample(stackSource);

            var fileParser = new MicrosoftWindowsKernelFileTraceEventParser(eventSource);

            fileParser.Create += delegate (FileIOCreateTraceData data)
            {
                StackSourceCallStackIndex stackIdx = GetStackForProcess(data.Process(), traceLog, stackSource, processStackCache);
                stackIdx = stackSource.GetCallStack(data.CallStackIndex(), stackIdx);
                string imageFrameString = string.Format("FileOpenOrCreate {0}", data.FileName);
                StackSourceFrameIndex imageFrameIdx = stackSource.Interner.FrameIntern(imageFrameString);
                stackIdx = stackSource.Interner.CallStackIntern(imageFrameIdx, stackIdx);

                sample.Count = 1;
                sample.Metric = 1;
                sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                sample.StackIndex = stackIdx;
                stackSource.AddSample(sample);
            };

            eventSource.Kernel.AddCallbackForEvents(delegate (ImageLoadTraceData data)
            {
                StackSourceCallStackIndex stackIdx = GetStackForProcess(data.Process(), traceLog, stackSource, processStackCache);
                stackIdx = stackSource.GetCallStack(data.CallStackIndex(), stackIdx);
                string fileCreateFrameString = string.Format("ImageLoad Base 0x{0:x} Size 0x{1:x} Name {2}", data.ImageBase, data.ImageSize, data.FileName);
                StackSourceFrameIndex fileCreateFrameIdx = stackSource.Interner.FrameIntern(fileCreateFrameString);
                stackIdx = stackSource.Interner.CallStackIntern(fileCreateFrameIdx, stackIdx);

                sample.Count = 1;
                sample.Metric = 1;
                sample.TimeRelativeMSec = data.TimeStampRelativeMSec;
                sample.StackIndex = stackIdx;
                stackSource.AddSample(sample);
            });

            eventSource.Process();
            stackSource.DoneAddingSamples();
            return stackSource;
        }
        private static StackSourceCallStackIndex GetStackForProcess(TraceProcess process, TraceLog traceLog, MutableTraceEventStackSource stackSource, StackSourceCallStackIndex[] processStackCache)
        {
            if (process == null)
                return StackSourceCallStackIndex.Invalid;

            var ret = processStackCache[(int)process.ProcessIndex];
            if (ret == StackSourceCallStackIndex.Invalid)
            {
                StackSourceCallStackIndex parentStack = StackSourceCallStackIndex.Invalid;
                // The ID check is because process 0 has itself as a parent, which creates a infinite recursion.      
                if (process.ProcessID != process.ParentID)
                {
                    parentStack = GetStackForProcess(process.Parent, traceLog, stackSource, processStackCache);
                }

                string parent = "";
                if (parentStack == StackSourceCallStackIndex.Invalid)
                    parent += ",Parent=" + process.ParentID;

                string command = process.CommandLine;
                if (string.IsNullOrWhiteSpace(command))
                    command = process.ImageFileName;
                string processFrameString = string.Format("Process({0}{1}): {2}", process.ProcessID, parent, command);

                StackSourceFrameIndex processFrameIdx = stackSource.Interner.FrameIntern(processFrameString);
                ret = stackSource.Interner.CallStackIntern(processFrameIdx, parentStack);
            }
            return ret;
        }

        private static string GetDirectoryName(string filePath)
        {
            // We need long (over 260) file name support so we do this by hand.  
            var lastSlash = filePath.LastIndexOf('\\');
            if (lastSlash < 0)
                return "";
            return filePath.Substring(0, lastSlash + 1);
        }

        private static string GetFileName(string filePath)
        {
            // We need long (over 260) file name support so we do this by hand.  
            var lastSlash = filePath.LastIndexOf('\\');
            if (lastSlash < 0)
                return filePath;
            return filePath.Substring(lastSlash + 1);
        }

        /// <summary>
        /// Implements a simple one-element cache for find the heap to look in.  
        /// </summary>
        private static Dictionary<Address, StackSourceSample> GetHeap(Address heapHandle, Dictionary<Address, Dictionary<Address, StackSourceSample>> heaps, ref Dictionary<Address, StackSourceSample> lastHeapAllocs, ref Address lastHeapHandle)
        {
            Dictionary<Address, StackSourceSample> ret;

            if (!heaps.TryGetValue(heapHandle, out ret))
            {
                ret = new Dictionary<Address, StackSourceSample>();
                heaps.Add(heapHandle, ret);
            }
            lastHeapHandle = heapHandle;
            lastHeapAllocs = ret;
            return ret;
        }

        private static void LogGCHandleLifetime(MutableTraceEventStackSource stackSource,
            StackSourceSample sample, GCHandleInfo info, double timeRelativeMSec, TextWriter log)
        {
            sample.Metric = (float)(timeRelativeMSec - info.PinStartTimeRelativeMSec);
            if (sample.Metric < 0)
            {
                log.WriteLine("Error got a negative time at {0:n3} started {1:n3}.  Dropping", timeRelativeMSec, info.PinStartTimeRelativeMSec);
                return;
            }

            var stackIndex = info.PinStack;
            var roundToLog = Math.Pow(10.0, Math.Ceiling(Math.Log10(sample.Metric)));
            var nodeName = "LIVE_FOR <= " + roundToLog + " msec";
            var nodeIndex = stackSource.Interner.FrameIntern(nodeName);
            stackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackIndex);

            nodeName = "OBJECT_INSTANCEID = " + info.ObjectAddress;
            nodeIndex = stackSource.Interner.FrameIntern(nodeName);
            stackIndex = stackSource.Interner.CallStackIntern(nodeIndex, stackIndex);

            sample.TimeRelativeMSec = info.PinStartTimeRelativeMSec;
            sample.StackIndex = stackIndex;
            stackSource.AddSample(sample);
        }

        class PerThreadGCHandleInfo
        {
            public Address LastHandle;
            public Address LastObject;
            public Address LikelyAsyncHandleTable1;
            public Address LikelyAsyncHandleTable2;
        }

        class GCHandleInfo
        {
            public double PinStartTimeRelativeMSec;
            public Address ObjectAddress;
            public StackSourceCallStackIndex PinStack = StackSourceCallStackIndex.Invalid;
            public bool IsAsync;
            public byte GCGen;
        }

        public override List<IProcess> GetProcesses(TextWriter log)
        {
            var processes = new List<IProcess>();

            var eventLog = GetTraceLog(log);
            foreach (var process in eventLog.Processes)
            {
                var iprocess = new IProcessForStackSource(process.Name);
                iprocess.StartTime = process.StartTime;
                iprocess.EndTime = process.EndTime;
                iprocess.CPUTimeMSec = process.CPUMSec;
                iprocess.ParentID = process.ParentID;
                iprocess.CommandLine = process.CommandLine;
                iprocess.ProcessID = process.ProcessID;
                processes.Add(iprocess);
            }
            processes.Sort();
            return processes;
        }


        /// <summary>
        /// Class keeps track of the memory state given virtual allocs.  Basically you have to model what memory is allocated 
        /// </summary>
        private class MemState
        {
            public MemState()
            {
                m_searchTable.Add(new Region(0, Region.FreeStackIndex, null));  // Memory starts out completely free.  
                m_numRegions = 1;
            }
            [Conditional("DEBUG")]
            private void ClassInvarient()
            {
                Debug.Assert(0 < m_searchTable.Count);
                var prev = m_searchTable[0];
                Debug.Assert(prev.MemAddr == 0);
                var cur = prev.Next;
                var regionCount = 1;        // Total number of regions in my linked list
                var curIdx = 1;             // Index in my sorted m_searchTable
                while (cur != null)
                {
                    // Update the curIdx.   Note that you can have multiple entries pointing to the same location (this is how we delete regions
                    // without having to shuffle the table.
                    while (curIdx < m_searchTable.Count && m_searchTable[curIdx] == cur)
                        curIdx++;
                    Debug.Assert(m_searchTable.Count <= curIdx || cur.MemAddr < m_searchTable[curIdx].MemAddr);

                    Debug.Assert(prev.MemAddr < cur.MemAddr);     // strictly increasing
                    Debug.Assert(!(cur.Next == null && cur.AllocStack != Region.FreeStackIndex && cur.MemAddr != ulong.MaxValue));
                    prev = cur;
                    cur = cur.Next;
                    regionCount++;
                }
                Debug.Assert(regionCount == m_numRegions);          // m_numRegions is accurate.  
                Debug.Assert(curIdx == m_searchTable.Count);        // One entries in the table are in the list.  
            }
#if DEBUG
            private int Count
            {
                get
                {
                    var cur = m_searchTable[0];
                    int cnt = 0;
                    while (cur != null)
                    {
                        cur = cur.Next;
                        cnt++;
                    }
                    return cnt;
                }
            }
#endif
#if DEBUG
            /// <summary>
            /// This routine is only used in asserts.   It represents the total amount of net memory that has been
            /// committed by all the VirtualAllocs/Frees that have occurred so far.  
            /// </summary>
            public long TotalMem
            {
                get
                {
                    long ret = 0;
                    var cur = m_searchTable[0];
                    while (cur != null)
                    {
                        if (!cur.IsFree)
                            ret += (long)(cur.Next.MemAddr - cur.MemAddr);
                        cur = cur.Next;
                    }
                    return ret;
                }
            }
#endif

            /// <summary>
            /// updates the memory state of [startAddr, startAddr+length) to be either allocated or free (based on 'isAlloc').  
            /// It returns the amount of memory delta (positive for allocation, negative for free).
            /// 
            /// What makes this a pain is VirtuaAlloc regions can overlap (you can 'commit' the same region multiple times, or
            /// free just a region within an allocation etc).   
            /// 
            /// Thus you have to keep track of exactly what is allocated (we keep a sorted list of regions), and do set operations
            /// on these regions.   This is what makes it non-trivial.  
            /// 
            /// if 'isAlloc' is true, then allocStack should be the stack at that allocation.  
            /// 
            /// 'callback' is called with two parameters (the net memory change (will be negative for frees), as well as the call
            /// stack for the ALLOCATION (even in the case of a free, it is the allocation stack that is logged).   
            /// 
            /// If an allocation overlaps with an existing allocation, only the NET allocation is indicated (the existing allocated
            /// region is subtracted out.   This means is is the 'last' allocation that gets 'charged' for a region.
            /// 
            /// The main point, however is that there is no double-counting and get 'perfect' matching of allocs and frees. 
            /// 
            /// There may be more than one callback issued if the given input region covers several previously allocated regions
            /// and thus need to be 'split up'.  In the case of a free, several callbacks could be issued because different 
            /// allocation call stacks were being freed in a single call.  
            /// </summary>
            internal void Update(Address startAddr, long length, bool isAlloc, StackSourceCallStackIndex allocStack,
                Action<long, StackSourceCallStackIndex> callback)
            {
                Debug.Assert(startAddr != 0);                   // No on can allocate this virtual address.
                if (startAddr == 0)
                    return;
                Address endAddr = startAddr + (Address)length;  // end of range
                if (endAddr == 0)                               // It is possible to wrap around (if you allocate the last region of memory. 
                    endAddr = ulong.MaxValue;                   // Avoid this case by adjust it down a bit.  
                Debug.Assert(endAddr > startAddr);
                if (!isAlloc)
                    allocStack = Region.FreeStackIndex;

                m_totalUpdates++;
#if DEBUG
                long memoryBeforeUpdate = TotalMem;
                long callBackNet = 0;               // How much we said the net allocation was for all the callbacks we make.  
#endif

                Debug.Assert(allocStack != StackSourceCallStackIndex.Invalid);
                // From time to time, update the search table to be 'perfect' if we see that chain length is too high.  
                if (m_totalUpdates > m_searchTable.Count && m_totalChainTraverals > MaxChainLength * m_totalUpdates)
                {
                    Debug.WriteLine(string.Format("Redoing Search table.  Num Regions {0} Table Size {1}  numUpdates {2} Average Chain Leng {3}",
                        m_numRegions, m_searchTable.Count, m_totalUpdates, m_totalChainTraverals / m_totalUpdates));
                    ExpandSearchTable();
                    m_totalUpdates = 0;
                    m_totalChainTraverals = 0;
                }

                int searchTableIdx;             // Points at prev or before.  
                m_searchTable.BinarySearch(startAddr - 1, out searchTableIdx, delegate (Address x, Region y) { return x.CompareTo(y.MemAddr); });
                Debug.Assert(0 <= searchTableIdx);          // Can't get -1 because 0 is the smallest number 
                Region prev = m_searchTable[searchTableIdx];

                Region cur = prev.Next;                         // current node
                Debug.Assert(prev.MemAddr <= startAddr);

                Debug.WriteLine(string.Format("Addr {0:x} idx {1} prev {2:x}", startAddr, searchTableIdx, prev.MemAddr));
                for (int chainLength = 0; ; chainLength++)      // the amount of searching I need to do after binary search 
                {
                    m_totalChainTraverals++;

                    // If we fall off the end, 'clone' split the last region into one that exactly overlaps the new region.  
                    if (cur == null)
                    {
                        prev.Next = cur = new Region(endAddr, prev.AllocStack, null);
                        m_numRegions++;
                        if (chainLength > MaxChainLength)
                            m_searchTable.Add(cur);
                    }

                    // Does the new region start after (or at) prev and strictly before than cur? (that is, does the region overlap with prev?)
                    if (startAddr < cur.MemAddr)
                    {
                        var prevAllocStack = prev.AllocStack;       // Remember this since we clobber it.  

                        // Can I reuse the node (it starts at exactly the right place, or it is the same stack 
                        // (which I can coalesce))
                        if (startAddr == prev.MemAddr || prevAllocStack == allocStack)
                            prev.AllocStack = allocStack;
                        else
                        {
                            prev.Next = new Region(startAddr, allocStack, cur);
                            m_numRegions++;
                            prev = prev.Next;
                        }

                        // Try to break up long chains in the search table.   
                        if (chainLength > MaxChainLength)
                        {
                            Debug.Assert(searchTableIdx < m_searchTable.Count);
                            if (searchTableIdx + 1 == m_searchTable.Count)
                                m_searchTable.Add(prev);
                            else
                            {
                                Debug.Assert(m_searchTable[searchTableIdx].MemAddr <= prev.MemAddr);
                                // Make sure we remain sorted.   Note that we can exceed the next slot in the table because
                                // the region we are inserting 'covers' many table entries.   
                                if (m_searchTable.Count <= searchTableIdx + 2 || prev.MemAddr < m_searchTable[searchTableIdx + 2].MemAddr)
                                    m_searchTable[searchTableIdx + 1] = prev;
                            }
                            searchTableIdx++;
                            chainLength = 0;
                        }

                        // net is the amount we are freeing or allocating for JUST THIS FIRST overlapped region (prev to cur)
                        // We start out assuming that the new region is bigger than the current region, so the net is the full current region.  
                        long net = (long)(cur.MemAddr - startAddr);

                        // Does the new region fit completely between prev and cur?  
                        bool overlapEnded = (endAddr <= cur.MemAddr);
                        if (overlapEnded)
                        {
                            net = (long)(endAddr - startAddr);
                            // If it does not end exactly, we need to end our chunk and resume the previous region.  
                            if (endAddr != cur.MemAddr && prevAllocStack != allocStack)
                            {
                                prev.Next = new Region(endAddr, prevAllocStack, cur);
                                m_numRegions++;
                            }
                        }
                        Debug.Assert(net >= 0);

                        // Log the delta to the callback.  
                        StackSourceCallStackIndex stackToLog;
                        if (allocStack != Region.FreeStackIndex)        // Is the update an allocation.  
                        {
                            if (prevAllocStack != Region.FreeStackIndex)
                                net = 0;                                // committing a committed region, do nothing
                            stackToLog = allocStack;
                        }
                        else    // The update is a free.  
                        {
                            if (prevAllocStack == Region.FreeStackIndex)
                                net = 0;                                // freeing a freed region, do nothing  
                            net = -net;                                 // frees have negative weight. 
                            stackToLog = prevAllocStack;                // We attribute the free to the allocation call stack  
                        }
                        ClassInvarient();

                        if (net != 0)                                   // Make callbacks to user code if there is any change.  
                        {
#if DEBUG
                            callBackNet += net;
#endif
                            callback(net, stackToLog);                  // issue the callback
                        }

                        if (overlapEnded || endAddr == 0)               // Are we done?  (endAddr == 0 is for the case where the region wraps around).  
                        {
#if DEBUG
                            Debug.Assert(memoryBeforeUpdate + callBackNet == TotalMem);
                            Debug.WriteLine(string.Format("EXITING Num Regions {0} Table Size {1}  numUpdates {2} Average Chain Len {3}",
                                m_numRegions, m_searchTable.Count, m_totalUpdates, m_totalChainTraverals * 1.0 / m_totalUpdates));
#endif
                            // Debug.Write("**** Exit State\r\n" + this.ToString());
                            return;
                        }

                        startAddr = cur.MemAddr;       // Modify the region so that it no longer includes the overlap with 'prev'  
                    }

                    // we may be able to coalesce (probably free) nodes.  
                    if (prev.AllocStack == cur.AllocStack)
                    {
                        prev.Next = cur.Next;       // Remove cur (prev does not move)
                        --m_numRegions;

                        // Make sure there are no entries in the search table that point at the entry to be deleted.   
                        var idx = searchTableIdx;
                        do
                        {
                            Debug.Assert(m_searchTable[idx].MemAddr <= cur.MemAddr);
                            if (cur == m_searchTable[idx])
                            {
                                // Assert that we stay sorted.  
                                Debug.Assert(idx == 0 || m_searchTable[idx - 1].MemAddr <= prev.MemAddr);
                                Debug.Assert(idx + 1 == m_searchTable.Count || prev.MemAddr <= m_searchTable[idx + 1].MemAddr);
                                m_searchTable[idx] = prev;
                            }
                            idx++;
                        } while (idx < m_searchTable.Count && m_searchTable[idx].MemAddr <= cur.MemAddr);

                        ClassInvarient();
                    }
                    else
                        prev = cur;                 // prev advances to cur 

                    cur = cur.Next;
                }
            }

            /// <summary>
            /// Allocate a new search table that has all the regions in it with not chaining necessary.   
            /// </summary>
            private void ExpandSearchTable()
            {
                Region ptr = m_searchTable[0];
                m_searchTable = new GrowableArray<Region>(m_numRegions + MaxChainLength);   // Add a bit more to grow on the end if necessary.  
                while (ptr != null)
                {
                    m_searchTable.Add(ptr);
                    ptr = ptr.Next;
                }
                Debug.Assert(m_searchTable.Count == m_numRegions);
            }

            const int MaxChainLength = 8;           // We don't want chain lengths bigger than this.  
            // The state of memory is represented as a (sorted) linked list of addresses (with a stack), 
            // Some of the regions are free (marked by FreeStackIndex)  They only have a start address so by 
            // construction they can't overlap.  
            class Region
            {
                // The special value that represents a free region.  
                public const StackSourceCallStackIndex FreeStackIndex = (StackSourceCallStackIndex)(-2);
                /// <summary>
                /// Create an allocation region starting at 'startAddr' allocated at 'allocStack'
                /// </summary>
                public Region(Address memAddr, StackSourceCallStackIndex allocStack, Region next) { MemAddr = memAddr; AllocStack = allocStack; Next = next; }
                public bool IsFree { get { return AllocStack == FreeStackIndex; } }

                public Address MemAddr;
                public StackSourceCallStackIndex AllocStack;
                public Region Next;
            };
#if DEBUG
            public override string ToString()
            {
                var sb = new StringBuilder();
                var cur = m_searchTable[0];
                while (cur != null)
                {
                    sb.Append("[").Append(cur.MemAddr.ToString("X")).Append(" stack=").Append(cur.AllocStack).Append("]").AppendLine();
                    cur = cur.Next;
                }
                return sb.ToString();
            }
#endif
            /// <summary>
            /// The basic data structure here is a linked list where each element is ALSO in this GrowableArray of
            /// entry points into that list.   This array of entry points is SORTED, so we can do binary search to 
            /// find a particular entry in log(N) time.   However we want to support fast insertion (and I am too
            /// lazy to implement a self-balancing tree) so when we add entries we add them to the linked list but
            /// not necessarily to this binary search table.   From time to time we will 'fixup' this table to 
            /// be perfect again.   
            /// </summary>
            GrowableArray<Region> m_searchTable;        // always non-empty, first entry is linked list to all entries.  

            // Keep track of enough to compute the average chain length on lookups.   
            int m_totalChainTraverals;                  // links we have to traverse from the search table to get to the entry we want.
            int m_totalUpdates;                         // Number of lookups we did.  (We reset after every table expansion).   
            int m_numRegions;                           // total number of entries in our linked list (may be larger than the search table) 
        }


        private static string[] AllocNames = InitAllocNames();
        private static string[] InitAllocNames()
        {
            // Cache the names, so we don't create them on every event.  
            string[] ret = new string[16];
            int size = 1;
            for (int i = 0; i < ret.Length; i++)
            {
                ret[i] = "Alloc < " + size;
                size *= 2;
            }
            return ret;
        }
        private static string GetAllocName(uint metric)
        {
            string allocName;
            int log2Metric = 0;
            while (metric > 0)
            {
                metric >>= 1;
                log2Metric++;
            }
            if (log2Metric < AllocNames.Length)
                allocName = AllocNames[log2Metric];
            else
                allocName = "Alloc >= 32768";
            return allocName;
        }
        #endregion

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            ConfigureAsEtwStackWindow(stackWindow, stackSourceName == "CPU");

            if (stackSourceName == "Processes / Files / Registry")
            {
                var defaultFold = @"^FileOpenOrCreate*:\Windows\Sys;^ImageLoad*:\Windows\Sys;^Process*conhost";
                stackWindow.FoldRegExTextBox.Items.Add(defaultFold);
                stackWindow.FoldRegExTextBox.Text = defaultFold;

                stackWindow.CallTreeTab.IsSelected = true;      // start with the call tree view
                return;
            }

            if (stackSourceName.Contains("(with Tasks)") || stackSourceName.Contains("(with StartStop Activities)"))
            {
                var taskFoldPatBase = "ntoskrnl!%ServiceCopyEnd;mscorlib%!System.Runtime.CompilerServices.Async%MethodBuilder";
                var taskFoldPat = taskFoldPatBase + ";^STARTING TASK";
                stackWindow.FoldRegExTextBox.Items.Add(taskFoldPat);
                stackWindow.FoldRegExTextBox.Items.Add(taskFoldPatBase);

                // If the new pattern is a superset of the old, then use it.  
                if (taskFoldPat.StartsWith(stackWindow.FoldRegExTextBox.Text))
                    stackWindow.FoldRegExTextBox.Text = taskFoldPat;

                stackWindow.GroupRegExTextBox.Items.Insert(0, @"[Nuget] System.%!=>OTHER;Microsoft.%!=>OTHER;mscorlib%=>OTHER;v4.0.30319%\%!=>OTHER;system32\*!=>OTHER;syswow64\*!=>OTHER");

                var excludePat = "LAST_BLOCK";
                stackWindow.ExcludeRegExTextBox.Items.Add(excludePat);
                stackWindow.ExcludeRegExTextBox.Items.Add("LAST_BLOCK;Microsoft.Owin.Host.SystemWeb!*IntegratedPipelineContextStage.BeginEvent;Microsoft.Owin.Host.SystemWeb!*IntegratedPipelineContextStage*RunApp");
                stackWindow.ExcludeRegExTextBox.Text = excludePat;
            }

            if (stackSourceName == "CPU" || stackSourceName.Contains("Thread Time"))
            {
                if (m_traceLog != null)
                    stackWindow.ExtraTopStats += " TotalProcs " + this.m_traceLog.NumberOfProcessors;
                stackWindow.ScalingPolicy = ScalingPolicyKind.TimeMetric;
                if (!stackSourceName.Contains("Thread Time"))
                    stackWindow.FoldPercentTextBox.Text = stackWindow.GetDefaultFoldPercentage();
            }

            if (stackSourceName == "Net OS Heap Alloc" || stackSourceName == "Image Load" || stackSourceName == "Disk I/O" ||
                stackSourceName == "File I/O" || stackSourceName == "Exceptions" || stackSourceName == "Managed Load" || stackSourceName.StartsWith("Process")
                || stackSourceName.StartsWith("Virtual") || stackSourceName == "Pinning" || stackSourceName.Contains("Thread Time"))
            {
                stackWindow.CallTreeTab.IsSelected = true;      // start with the call tree view
            }

            if (stackSourceName == "Pinning")
            {
                string defaultFoldPattern = "OBJECT_INSTANCEID;LIVE_FOR";
                stackWindow.FoldRegExTextBox.Text = defaultFoldPattern;
                stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFoldPattern);
            }

            if (stackSourceName == "Pinning At GC Time")
            {
                string defaultFoldPattern = "^PINNED_FOR;^GC_NUM";
                stackWindow.FoldRegExTextBox.Text = defaultFoldPattern;
                stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFoldPattern);

                stackWindow.GroupRegExTextBox.Text = "mscorlib.ni!->;system.ni!->;{%}!=>module $1;Generation 0->NonGen2;Generation 1->NonGen2;Type System.Byte[]->Type System.Byte[]";
                stackWindow.ExcludeRegExTextBox.Items.Insert(0, "PinnableBufferCache.CreateNewBuffers");
            }

            if (stackSourceName.Contains("Ref Count"))
            {
                stackWindow.RemoveColumn("IncPercentColumn");
                stackWindow.RemoveColumn("ExcPercentColumn");
            }

            if (stackSourceName == "CCW Ref Count")
            {
                string defaultFoldPattern = "CCW NewRefCnt;CCW AddRef;CCW Release";
                stackWindow.FoldRegExTextBox.Text = defaultFoldPattern;
                stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFoldPattern);
                stackWindow.FoldRegExTextBox.Items.Insert(1, "CCW NewRefCnt");
            }

            if ((stackSourceName == "Heap Snapshot Pinning") || (stackSourceName == "Heap Snapshot Pinned Object Allocation"))
            {
                string defaultFoldPattern = "OBJECT_INSTANCE";
                stackWindow.FoldRegExTextBox.Text = defaultFoldPattern;
                stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFoldPattern);
            }

            if (stackSourceName == "Net OS Heap Alloc" || stackSourceName.StartsWith("GC Heap Net Mem") ||
                stackSourceName.StartsWith("Virtual") || stackSourceName.StartsWith("GC Heap Alloc Ignore Free"))
                stackWindow.ComputeMaxInTopStats = true;

            if (stackSourceName == "Net OS Heap Alloc")
                stackWindow.FoldRegExTextBox.Items.Insert(0, "^Alloc");

            if (stackSourceName.StartsWith("ASP.NET Thread Time"))
            {
                var prev = stackWindow.FoldRegExTextBox.Text;
                if (0 < prev.Length)
                    prev += ";";
                prev += "^Request URL";
                stackWindow.FoldRegExTextBox.Text = prev;
                stackWindow.FoldRegExTextBox.Items.Insert(0, prev);
            }

            if (m_extraTopStats != null)
                stackWindow.ExtraTopStats = m_extraTopStats;
        }
        public override bool SupportsProcesses { get { return true; } }

        /// <summary>
        /// Find symbols for the simple module name 'simpleModuleName.  If 'processId' is non-zero then only search for modules loaded in that
        /// process, otherwise look systemWide.  
        /// </summary>
        public override void LookupSymbolsForModule(string simpleModuleName, TextWriter log, int processId = 0)
        {
            var symReader = GetSymbolReader(log);

            // Remove any extensions.  
            simpleModuleName = Path.GetFileNameWithoutExtension(simpleModuleName);

            // If we have a process, look the DLL up just there
            var moduleFiles = new Dictionary<int, TraceModuleFile>();
            if (processId != 0)
            {
                var process = m_traceLog.Processes.LastProcessWithID(processId);
                if (process != null)
                {
                    foreach (var loadedModule in process.LoadedModules)
                    {
                        var baseName = Path.GetFileNameWithoutExtension(loadedModule.Name);
                        if (string.Compare(baseName, simpleModuleName, StringComparison.OrdinalIgnoreCase) == 0)
                            moduleFiles[(int)loadedModule.ModuleFile.ModuleFileIndex] = loadedModule.ModuleFile;
                    }
                }
            }

            // We did not find it, try system-wide
            if (moduleFiles.Count == 0)
            {
                foreach (var moduleFile in m_traceLog.ModuleFiles)
                {
                    var baseName = Path.GetFileNameWithoutExtension(moduleFile.Name);
                    if (string.Compare(baseName, simpleModuleName, StringComparison.OrdinalIgnoreCase) == 0)
                        moduleFiles[(int)moduleFile.ModuleFileIndex] = moduleFile;
                }
            }

            if (moduleFiles.Count == 0)
                throw new ApplicationException("Could not find module " + simpleModuleName + " in trace.");

            if (moduleFiles.Count > 1)
                log.WriteLine("Found {0} modules with name {1}", moduleFiles.Count, simpleModuleName);
            foreach (var moduleFile in moduleFiles.Values)
            {
                try
                {
                    m_traceLog.CodeAddresses.LookupSymbolsForModule(symReader, moduleFile);
                }
                catch (ApplicationException ex)
                {
                    log.WriteLine("Error looking up " + moduleFile.FilePath + "\r\n    " + ex.Message);
                }
            }
        }
        public SymbolReader GetSymbolReader(TextWriter log, SymbolReaderOptions symbolFlags = SymbolReaderOptions.None)
        {
            return App.GetSymbolReader(FilePath, symbolFlags);
        }

        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            var tracelog = GetTraceLog(worker.LogWriter, delegate (bool truncated, int numberOfLostEvents, int eventCountAtTrucation)
            {
                if (!m_notifiedAboutLostEvents)
                {
                    HandleLostEvents(parentWindow, truncated, numberOfLostEvents, eventCountAtTrucation, worker);
                    m_notifiedAboutLostEvents = true;
                }
            });

            // Warn about possible Win8 incompatibility.  
            var logVer = tracelog.OSVersion.Major * 10 + tracelog.OSVersion.Minor;
            if (62 <= logVer)
            {
                var ver = Environment.OSVersion.Version.Major * 10 + Environment.OSVersion.Version.Minor;
                if (ver < 62)       // We are decoding on less than windows 8
                {
                    if (!m_notifiedAboutWin8)
                    {
                        m_notifiedAboutWin8 = true;
                        var versionMismatchWarning = "This trace was captured on Window 8 and is being read\r\n" +
                                                     "on and earlier OS.  If you experience any problems please\r\n" +
                                                     "read the trace on an Windows 8 OS.";
                        worker.LogWriter.WriteLine(versionMismatchWarning);
                        parentWindow.Dispatcher.BeginInvoke((Action)delegate ()
                        {
                            MessageBox.Show(parentWindow, versionMismatchWarning, "Log File Version Mismatch", MessageBoxButton.OK);
                        });
                    }
                }
            }

            var advanced = new PerfViewTreeGroup("Advanced Group");
            var memory = new PerfViewTreeGroup("Memory Group");
            var obsolete = new PerfViewTreeGroup("Old Group");
            m_Children = new List<PerfViewTreeItem>();

            bool hasCPUStacks = false;
            bool hasDllStacks = false;
            bool hasCSwitchStacks = false;
            bool hasReadyThreadStacks = false;
            bool hasHeapStacks = false;
            bool hasGCAllocationTicks = false;
            bool hasExceptions = false;
            bool hasManagedLoads = false;
            bool hasAspNet = false;
            bool hasDiskStacks = false;
            bool hasAnyStacks = false;
            bool hasVirtAllocStacks = false;
            bool hasFileStacks = false;
            bool hasTpl = false;
            bool hasTplStacks = false;
            bool hasGCHandleStacks = false;
            bool hasMemAllocStacks = false;
            bool hasJSHeapDumps = false;
            bool hasDotNetHeapDumps = false;
            bool hasWCFRequests = false;
            bool hasCCWRefCountStacks = false;
            bool hasWindowsRefCountStacks = false;
            bool hasPinObjectAtGCTime = false;
            bool hasObjectUpdate = false;
            bool hasGCEvents = false;
            bool hasProjectNExecutionTracingEvents = false;
            bool hasProcessEvents = false;

            var stackEvents = new List<TraceEventCounts>();
            foreach (var counts in tracelog.Stats)
            {
                var name = counts.EventName;
                if (!hasCPUStacks && name.StartsWith("PerfInfo"))
                    hasCPUStacks = true;                // Even without true stacks we can display something in the stack viewer.  
                if (!hasAspNet && name.StartsWith("AspNetReq"))
                    hasAspNet = true;
                if (counts.ProviderGuid == ApplicationServerTraceEventParser.ProviderGuid)
                    hasWCFRequests = true;
                if (name.StartsWith("JSDumpHeapEnvelope"))
                    hasJSHeapDumps = true;
                if (name.StartsWith("GC/Start"))
                    hasGCEvents = true;
                if (name.StartsWith("Process/Start") || name.StartsWith("ProcessStart/Start") || name.StartsWith("Process/DCStop"))
                    hasProcessEvents = true;

                if (name.StartsWith("GC/BulkNode"))
                    hasDotNetHeapDumps = true;

                if (name.StartsWith("GC/PinObjectAtGCTime"))
                    hasPinObjectAtGCTime = true;

                if (name.StartsWith("GC/BulkSurvivingObjectRanges") || name.StartsWith("GC/BulkMovedObjectRanges"))
                    hasObjectUpdate = true;

                if (counts.ProviderGuid == TplEtwProviderTraceEventParser.ProviderGuid)
                    hasTpl = true;

                if (counts.StackCount > 0)
                {
                    hasAnyStacks = true;
                    if (counts.ProviderGuid == ETWClrProfilerTraceEventParser.ProviderGuid && name.StartsWith("ObjectAllocated"))
                        hasMemAllocStacks = true;
                    if (name.StartsWith("GC/SampledObjectAllocation"))
                        hasMemAllocStacks = true;
                    if (name.StartsWith("GC/CCWRefCountChange"))
                        hasCCWRefCountStacks = true;
                    if (name.StartsWith("Object/CreateHandle"))
                        hasWindowsRefCountStacks = true;
                    if (name.StartsWith("Image"))
                        hasDllStacks = true;
                    if (name.StartsWith("HeapTrace"))
                        hasHeapStacks = true;
                    if (name.StartsWith("Thread/CSwitch"))
                        hasCSwitchStacks = true;
                    if (name.StartsWith("GC/AllocationTick"))
                        hasGCAllocationTicks = true;
                    if (name.StartsWith("Exception") || name.StartsWith("PageFault/AccessViolation"))
                        hasExceptions = true;
                    if (name.StartsWith("GC/SetGCHandle"))
                        hasGCHandleStacks = true;
                    if (name.StartsWith("Loader/ModuleLoad"))
                        hasManagedLoads = true;
                    if (name.StartsWith("VirtualMem"))
                        hasVirtAllocStacks = true;
                    if (name.StartsWith("Dispatcher/ReadyThread"))
                        hasReadyThreadStacks = true;
                    if (counts.ProviderGuid == TplEtwProviderTraceEventParser.ProviderGuid)
                        hasTplStacks = true;

                    if (name.StartsWith("DiskIO"))
                        hasDiskStacks = true;
                    if (name.StartsWith("FileIO"))
                        hasFileStacks = true;
                    if (name.StartsWith("MethodEntry"))
                        hasProjectNExecutionTracingEvents = true;
                }
            }

            m_Children.Add(new PerfViewTraceInfo(this));
            m_Children.Add(new PerfViewProcesses(this));
            m_Children.Add(new PerfViewStackSource(this, "Processes / Files / Registry") { SkipSelectProcess = true });

            if (hasCPUStacks)
                m_Children.Add(new PerfViewStackSource(this, "CPU"));
            if (hasCSwitchStacks)
            {
                if (hasTplStacks)
                {
                    advanced.Children.Add(new PerfViewStackSource(this, "Thread Time"));
                    advanced.Children.Add(new PerfViewStackSource(this, "Thread Time (with Tasks)"));
                    m_Children.Add(new PerfViewStackSource(this, "Thread Time (with StartStop Activities)"));
                }
                else
                    m_Children.Add(new PerfViewStackSource(this, "Thread Time"));
                if (hasReadyThreadStacks)
                    advanced.Children.Add(new PerfViewStackSource(this, "Thread Time (with ReadyThread)"));
            }

            if (hasDiskStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "Disk I/O"));
            if (hasFileStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "File I/O"));

            if (hasHeapStacks)
                memory.Children.Add(new PerfViewStackSource(this, "Net OS Heap Alloc"));
            if (hasVirtAllocStacks)
            {
                memory.Children.Add(new PerfViewStackSource(this, "Net Virtual Alloc"));
                memory.Children.Add(new PerfViewStackSource(this, "Net Virtual Reserve"));
            }
            if (hasGCAllocationTicks)
            {
                if (hasObjectUpdate)
                    memory.Children.Add(new PerfViewStackSource(this, "GC Heap Net Mem (Coarse Sampling)"));
                memory.Children.Add(new PerfViewStackSource(this, "GC Heap Alloc Ignore Free (Coarse Sampling)"));
            }
            if (hasMemAllocStacks)
            {
                memory.Children.Add(new PerfViewStackSource(this, "GC Heap Net Mem"));
                memory.Children.Add(new PerfViewStackSource(this, "GC Heap Alloc Ignore Free"));
                memory.Children.Add(new PerfViewStackSource(this, "Gen 2 Object Deaths"));
            }

            if (hasDllStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "Image Load"));
            if (hasManagedLoads)
                advanced.Children.Add(new PerfViewStackSource(this, "Managed Load"));
            if (hasExceptions)
                advanced.Children.Add(new PerfViewStackSource(this, "Exceptions"));
            if (hasGCHandleStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "Pinning"));
            if (hasPinObjectAtGCTime)
                advanced.Children.Add(new PerfViewStackSource(this, "Pinning At GC Time"));

            if (hasGCEvents && hasCPUStacks && AppLog.InternalUser)
                memory.Children.Add(new PerfViewStackSource(this, "Server GC"));

            if (hasCCWRefCountStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "CCW Ref Count"));

            if (hasWindowsRefCountStacks)
                advanced.Children.Add(new PerfViewStackSource(this, "Windows Handle Ref Count"));

            if (hasGCHandleStacks && hasMemAllocStacks)
            {
                bool matchingHeapSnapshotExists = GCPinnedObjectAnalyzer.ExistsMatchingHeapSnapshot(this.FilePath);
                if (matchingHeapSnapshotExists)
                {
                    advanced.Children.Add(new PerfViewStackSource(this, "Heap Snapshot Pinning"));
                    advanced.Children.Add(new PerfViewStackSource(this, "Heap Snapshot Pinned Object Allocation"));
                }
            }

            if ((hasAspNet) || (hasWCFRequests))
            {
                if (hasCPUStacks)
                {
                    obsolete.Children.Add(new PerfViewStackSource(this, "Server Request CPU"));
                }
                if (hasCSwitchStacks)
                {
                    obsolete.Children.Add(new PerfViewStackSource(this, "Server Request Thread Time"));
                }
                if (hasGCAllocationTicks)
                {
                    obsolete.Children.Add(new PerfViewStackSource(this, "Server Request Managed Allocation"));
                }
            }

            if (hasAnyStacks)
            {
                advanced.Children.Add(new PerfViewStackSource(this, "Any"));
                if (hasTpl)
                {
                    if (hasCSwitchStacks)
                    {
                        advanced.Children.Add(new PerfViewStackSource(this, "Any Stacks (with Tasks)"));
                        advanced.Children.Add(new PerfViewStackSource(this, "Any Stacks (with StartStop Activities)"));
                        advanced.Children.Add(new PerfViewStackSource(this, "Any StartStopTree"));
                    }
                    advanced.Children.Add(new PerfViewStackSource(this, "Any TaskTree"));
                }
            }

            if (hasAspNet)
            {
                advanced.Children.Add(new PerfViewAspNetStats(this));
                if (hasCPUStacks)
                {
                    var name = "ASP.NET Thread Time";
                    if (hasCSwitchStacks && hasTplStacks)
                    {
                        obsolete.Children.Add(new PerfViewStackSource(this, "ASP.NET Thread Time (with Tasks)"));
                    }
                    else
                        name += " (CPU ONLY)";
                    obsolete.Children.Add(new PerfViewStackSource(this, name));
                }
            }

            if (hasProjectNExecutionTracingEvents && AppLog.InternalUser)
            {
                advanced.Children.Add(new PerfViewStackSource(this, "Execution Tracing"));
            }

            memory.Children.Add(new PerfViewGCStats(this));

            // TODO currently this is experimental enough that we don't show it publicly.  
            if (AppLog.InternalUser)
                memory.Children.Add(new MemoryAnalyzer(this));

            if (hasJSHeapDumps || hasDotNetHeapDumps)
                memory.Children.Add(new PerfViewHeapSnapshots(this));

            advanced.Children.Add(new PerfViewJitStats(this));

            advanced.Children.Add(new PerfViewEventStats(this));

            m_Children.Add(new PerfViewEventSource(this));

            if (0 < memory.Children.Count)
                m_Children.Add(memory);
            if (0 < advanced.Children.Count)
                m_Children.Add(advanced);
            if (0 < obsolete.Children.Count)
                m_Children.Add(obsolete);

            return null;
        }
        // public override string DefaultStackSourceName { get { return "CPU"; } }

        public TraceLog GetTraceLog(TextWriter log, Action<bool, int, int> onLostEvents = null)
        {
            if (m_traceLog != null)
            {
                if (IsUpToDate)
                    return m_traceLog;
                m_traceLog.Dispose();
                m_traceLog = null;
            }
            var dataFileName = FilePath;
            var options = new TraceLogOptions();
            options.ConversionLog = log;
            if (App.CommandLineArgs.KeepAllEvents)
                options.KeepAllEvents = true;
            options.MaxEventCount = App.CommandLineArgs.MaxEventCount;
            options.SkipMSec = App.CommandLineArgs.SkipMSec;
            options.OnLostEvents = onLostEvents;
            options.LocalSymbolsOnly = false;
            options.ShouldResolveSymbols = delegate (string moduleFilePath) { return false; };       // Don't resolve any symbols

            // But if there is a directory called EtwManifests exists, look in there instead. 
            var etwManifestDirPath = Path.Combine(Path.GetDirectoryName(dataFileName), "EtwManifests");
            if (Directory.Exists(etwManifestDirPath))
                options.ExplicitManifestDir = etwManifestDirPath;

            UnZipIfNecessary(ref dataFileName, log);

            var etlxFile = dataFileName;
            var cachedEtlxFile = false;
            if (dataFileName.EndsWith(".etl", StringComparison.OrdinalIgnoreCase))
            {
                etlxFile = CacheFiles.FindFile(dataFileName, ".etlx");
                if (!File.Exists(etlxFile))
                {
                    log.WriteLine("Creating ETLX file {0} from {1}", etlxFile, dataFileName);
                    TraceLog.CreateFromEventTraceLogFile(dataFileName, etlxFile, options);

                    var dataFileSize = "Unknown";
                    if (File.Exists(dataFileName))
                        dataFileSize = ((new System.IO.FileInfo(dataFileName)).Length / 1000000.0).ToString("n3") + " MB";

                    log.WriteLine("ETL Size {0} ETLX Size {1:n3} MB", dataFileSize, (new System.IO.FileInfo(etlxFile)).Length / 1000000.0);
                }
                else
                {
                    cachedEtlxFile = true;
                    log.WriteLine("Found a corresponding ETLX file {0}", etlxFile);
                }
            }

            try
            {
                m_traceLog = new TraceLog(etlxFile);

                // Add some more parser that we would like.  
                new ETWClrProfilerTraceEventParser(m_traceLog);
                new MicrosoftWindowsNDISPacketCaptureTraceEventParser(m_traceLog);
            }
            catch (Exception)
            {
                if (cachedEtlxFile)
                {
                    // Delete the file and try again.  
                    FileUtilities.ForceDelete(etlxFile);
                    if (!File.Exists(etlxFile))
                        return GetTraceLog(log, onLostEvents);
                }
                throw;
            }

            m_utcLastWriteAtOpen = File.GetLastWriteTimeUtc(FilePath);
            if (App.CommandLineArgs.UnsafePDBMatch)
                m_traceLog.CodeAddresses.UnsafePDBMatching = true;

            if (m_traceLog.Truncated)   // Warn about truncation.  
            {
                GuiApp.MainWindow.Dispatcher.BeginInvoke((Action)delegate ()
                {
                    MessageBox.Show("The ETL file was too big to convert and was truncated.\r\nSee log for details", "Log File Truncated", MessageBoxButton.OK);
                });
            }
            return m_traceLog;
        }
        public TraceLog TryGetTraceLog() { return m_traceLog; }

        private void HandleLostEvents(Window parentWindow, bool truncated, int numberOfLostEvents, int eventCountAtTrucation, StatusBar worker)
        {
            string warning;
            if (!truncated)
            {
                // TODO see if we can get the buffer size out of the ETL file to give a good number in the message. 
                warning = "WARNING: There were " + numberOfLostEvents + " lost events in the trace.\r\n" +
                    "Some analysis might be invalid.\r\n" +
                    "Use /InMemoryCircularBuffer    to avoid this in future traces.";
            }
            else
            {
                warning = "WARNING: The ETLX file was truncated at " + eventCountAtTrucation + " events.\r\n" +
                    "This is to keep the ETLX file size under 4GB, however all rundown events are processed.\r\n" +
                    "Use /SkipMSec:XXX to see the later parts of the file.\r\n" +
                    "See log for more details.";
            }

            MessageBoxResult result = MessageBoxResult.None;
            parentWindow.Dispatcher.BeginInvoke((Action)delegate ()
            {
                result = MessageBox.Show(parentWindow, warning, "Lost Events", MessageBoxButton.OKCancel);
                worker.LogWriter.WriteLine(warning);
                if (result != MessageBoxResult.OK)
                    worker.AbortWork();
            });
        }
        public override void Close()
        {
            if (m_traceLog != null)
            {
                m_traceLog.Dispose();
                m_traceLog = null;
            }
            base.Close();
        }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FileBitmapImage"] as ImageSource; } }

        #region private
        /// <summary>
        /// See if the log has events from VS providers.  If so we should register the VS providers. 
        /// </summary>
        private bool HasVSEvents(TraceLog traceLog)
        {
            if (!m_checkedForVSEvents)
            {
                var codeMarkerGuid = new Guid(0x143A31DB, 0x0372, 0x40B6, 0xB8, 0xF1, 0xB4, 0xB1, 0x6A, 0xDB, 0x5F, 0x54);
                var measurementBlockGuid = new Guid(0x641D7F6C, 0x481C, 0x42E8, 0xAB, 0x7E, 0xD1, 0x8D, 0xC5, 0xE5, 0xCB, 0x9E);
                foreach (var stats in traceLog.Stats)
                    if (stats.ProviderGuid == codeMarkerGuid || stats.ProviderGuid == measurementBlockGuid)
                    {
                        m_hasVSEvents = true;
                        break;
                    }
                m_checkedForVSEvents = true;
            }
            return m_hasVSEvents;
        }
        bool m_checkedForVSEvents;
        bool m_hasVSEvents;

        internal static void UnZipIfNecessary(ref string inputFileName, TextWriter log, bool unpackInCache = true, bool wprConventions = false)
        {
            if (inputFileName.EndsWith(".trace.zip", StringComparison.OrdinalIgnoreCase))
            {
                log.WriteLine($"'{inputFileName}' is a linux trace.");
                return;
            }

            var extension = Path.GetExtension(inputFileName);
            if (string.Compare(extension, ".zip", StringComparison.OrdinalIgnoreCase) == 0 ||
                string.Compare(extension, ".vspx", StringComparison.OrdinalIgnoreCase) == 0)
            {
                string unzipedEtlFile;
                if (unpackInCache)
                {
                    unzipedEtlFile = CacheFiles.FindFile(inputFileName, ".etl");
                    if (File.Exists(unzipedEtlFile) && File.GetLastWriteTimeUtc(inputFileName) <= File.GetLastWriteTimeUtc(unzipedEtlFile))
                    {
                        log.WriteLine("Found a existing unzipped file {0}", unzipedEtlFile);
                        inputFileName = unzipedEtlFile;
                        return;
                    }
                }
                else
                {
                    if (inputFileName.EndsWith(".etl.zip", StringComparison.OrdinalIgnoreCase))
                        unzipedEtlFile = inputFileName.Substring(0, inputFileName.Length - 4);
                    else if (inputFileName.EndsWith(".vspx", StringComparison.OrdinalIgnoreCase))
                        unzipedEtlFile = Path.ChangeExtension(inputFileName, ".etl");
                    else
                        throw new ApplicationException("File does not end with the .etl.zip file extension");
                }

                ZippedETLReader etlReader = new ZippedETLReader(inputFileName, log);
                etlReader.EtlFileName = unzipedEtlFile;

                // Figure out where to put the symbols.  
                if (wprConventions)
                    etlReader.SymbolDirectory = Path.ChangeExtension(inputFileName, ".ngenpdb");
                else
                {
                    var inputDir = Path.GetDirectoryName(inputFileName);
                    if (inputDir.Length == 0)
                        inputDir = ".";
                    var symbolsDir = Path.Combine(inputDir, "symbols");
                    if (Directory.Exists(symbolsDir))
                        etlReader.SymbolDirectory = symbolsDir;
                    else
                        etlReader.SymbolDirectory = new SymbolPath(App.SymbolPath).DefaultSymbolCache();
                }
                log.WriteLine("Putting symbols in {0}", etlReader.SymbolDirectory);

                etlReader.UnpackAchive();
                inputFileName = unzipedEtlFile;
            }
        }

        TraceLog m_traceLog;
        bool m_notifiedAboutLostEvents;
        bool m_notifiedAboutWin8;
        string m_extraTopStats;
        #endregion
    }

    class WTPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "CDB WT calls"; } }
        public override string[] FileExtensions { get { return new string[] { ".wt" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            return new WTStackSource(FilePath);
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.FoldPercentTextBox.Text = stackWindow.GetDefaultFoldPercentage();
            stackWindow.ScalingPolicy = ScalingPolicyKind.TimeMetric;
        }
    }

    class OffProfPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Office Profiler"; } }
        public override string[] FileExtensions { get { return new string[] { ".offtree" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            return new OffProfStackSource(FilePath);
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.FoldPercentTextBox.Text = stackWindow.GetDefaultFoldPercentage();
            stackWindow.RemoveColumn("WhenColumn");
            stackWindow.RemoveColumn("FirstColumn");
            stackWindow.RemoveColumn("LastColumn");
        }
    }

    class DebuggerStackPerfViewFile : PerfViewFile
    {
        public override string FormatName
        {
            get { return "Windbg kc Call stack"; }
        }

        public override string[] FileExtensions
        {
            get { return new string[] { ".cdbStack", ".windbgStack" }; }
        }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            return new DebuggerStackSource(FilePath);
        }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.RemoveColumn("IncCountColumn");
            stackWindow.RemoveColumn("ExcCountColumn");
            stackWindow.RemoveColumn("FoldCountColumn");
        }
    }

    class XmlPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "PerfView XML FILE"; } }
        public override string[] FileExtensions { get { return new string[] { ".perfView.xml", ".perfView.xml.zip", ".perfView.json", ".perfView.json.zip" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            m_guiState = new StackWindowGuiState();

            return new XmlStackSource(FilePath, delegate (XmlReader reader)
            {
                if (reader.Name == "StackWindowGuiState")
                    m_guiState = m_guiState.ReadFromXml(reader);
                // These are only here for backward compatibility
                else if (reader.Name == "FilterXml")
                    m_guiState.FilterGuiState.ReadFromXml(reader);
                else if (reader.Name == "Log")
                    m_guiState.Log = reader.ReadElementContentAsString().Trim();
                else if (reader.Name == "Notes")
                    m_guiState.Notes = reader.ReadElementContentAsString().Trim();
                else
                    reader.Read();
            });
        }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.RestoreWindow(m_guiState, FilePath);
        }
        protected internal override void FirstAction(StackWindow stackWindow)
        {
            if (m_guiState != null)
                stackWindow.GuiState = m_guiState;

            m_guiState = null;
        }

        public override void LookupSymbolsForModule(string simpleModuleName, TextWriter log, int processId = 0)
        {
            throw new ApplicationException("Symbols can not be looked up after a stack view has been saved.\r\n" +
                "You must resolve all symbols you need before saving.\r\n" +
                "Consider the right click -> Lookup Warm Symbols command");
        }

        StackWindowGuiState m_guiState;
    }

    class VmmapPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Vmmap data file"; } }
        public override string[] FileExtensions { get { return new string[] { ".mmp" }; } }


        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            using (Stream dataStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                var xmlStream = dataStream;
                XmlReaderSettings settings = new XmlReaderSettings() { IgnoreWhitespace = true, IgnoreComments = true };
                using (XmlReader reader = XmlTextReader.Create(xmlStream, settings))
                    return new VMMapStackSource(reader);
            }
        }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            var defaultFold = "^Block;^Shareable;^Image Section;^Heap;^Private data;^Thread Stack";
            stackWindow.FoldRegExTextBox.Items.Insert(0, "^Images in");
            stackWindow.FoldRegExTextBox.Items.Insert(0, defaultFold);

            stackWindow.IncludeRegExTextBox.Items.Insert(0, "^Images in;^MappedFiles in");
            stackWindow.IncludeRegExTextBox.Items.Insert(0, "^Block Private");

            stackWindow.GroupRegExTextBox.Items.Insert(0, "[group files] ^MappedFile{*}->Image$1;^Image{*}->Image$1;Group MappedFile->Group Image;Group Image->Group Image");
        }
        protected internal override void FirstAction(StackWindow stackWindow)
        {
            stackWindow.CallTreeTab.IsSelected = true;
        }

        #region private
        [Flags]
        enum PageProtection
        {
            PAGE_EXECUTE = 0x10,
            PAGE_EXECUTE_READ = 0x20,
            PAGE_EXECUTE_READWRITE = 0x40,
            PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_NOACCESS = 0x01,
            PAGE_READONLY = 0x02,
            PAGE_READWRITE = 0x04,
            PAGE_WRITECOPY = 0x08,
        }

        enum UseType
        {
            Heap = 0,
            Stack = 1,
            Image = 2,
            MappedFile = 3,
            PrivateData = 4,
            Shareable = 5,
            Free = 6,
            // Unknown1 = 7,
            ManagedHeaps = 8,
            // Unknown2 = 9,
            Unusable = 10,
        }

        class MemoryNode
        {
            public static MemoryNode Root()
            {
                var ret = new MemoryNode();
                ret.Size = ulong.MaxValue;
                ret.Details = "[ROOT]";
                return ret;
            }
            public MemoryNode Add(XmlReader reader)
            {
                var newNode = new MemoryNode(reader);
                Insert(newNode);
                return newNode;
            }

            public ulong End { get { return Address + Size; } }
            public ulong Address;
            public ulong Blocks;
            public ulong ShareableWS;
            public ulong SharedWS;
            public ulong Size;
            public ulong Commit;
            public ulong PrivateBytes;
            public ulong PrivateWS;
            public ulong Id;
            public PageProtection Protection;
            public ulong Storage;
            public UseType UseType;
            public string Type;
            public string Details;
            public List<MemoryNode> Children;
            public MemoryNode Parent;

            public override string ToString()
            {
                return string.Format("<MemoryNode Name=\"{0}\" Start=\"0x{1:x}\" Length=\"0x{2:x}\"/>", Details, Address, Size);
            }

            #region private

            private void Insert(MemoryNode newNode)
            {
                Debug.Assert(Address <= newNode.Address && newNode.End <= End);
                if (Children == null)
                    Children = new List<MemoryNode>();

                // Search backwards for efficiency.  
                for (int i = Children.Count; 0 < i;)
                {
                    var child = Children[--i];
                    if (child.Address <= newNode.Address && newNode.End <= child.End)
                    {
                        child.Insert(newNode);
                        return;
                    }
                }
                Children.Add(newNode);
                newNode.Parent = this;
            }
            private MemoryNode() { }
            private MemoryNode(XmlReader reader)
            {
                Address = FetchLong(reader, "Address");
                Blocks = FetchLong(reader, "Blocks");
                ShareableWS = FetchLong(reader, "ShareableWS");
                SharedWS = FetchLong(reader, "SharedWS");
                Size = FetchLong(reader, "Size");
                Commit = FetchLong(reader, "Commit");
                PrivateBytes = FetchLong(reader, "PrivateBytes");
                PrivateWS = FetchLong(reader, "PrivateWS");
                Id = FetchLong(reader, "Id");     // This identifies the heap (for Heap type data)
                Protection = (PageProtection)int.Parse(reader.GetAttribute("Protection") ?? "0");
                Storage = FetchLong(reader, "Storage");
                UseType = (UseType)int.Parse(reader.GetAttribute("UseType") ?? "0");
                Type = reader.GetAttribute("Type") ?? "";
                Details = reader.GetAttribute("Details") ?? "";
            }

            static ulong FetchLong(XmlReader reader, string attributeName)
            {
                ulong ret = 0L;
                var attrValue = reader.GetAttribute(attributeName);
                if (attrValue != null)
                    ulong.TryParse(attrValue, out ret);
                return ret;
            }
            #endregion
        }

        class VMMapStackSource : InternStackSource
        {
            public VMMapStackSource(XmlReader reader)
            {
                m_sample = new StackSourceSample(this);
                MemoryNode top = MemoryNode.Root();
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        // Start over if we see another snapshot.  THus we read the last one.   
                        // THis is present VMMAP behavior.  TODO We should think about doing better.   
                        if (reader.Name == "Snapshot")
                            top = MemoryNode.Root();
                        else if (reader.Name == "Region")
                            top.Add(reader);
                    }
                }

                foreach (var child in top.Children)
                    AddToSource(child, StackSourceCallStackIndex.Invalid);
                Interner.DoneInterning();
            }

            /// <summary>
            /// Add all the nodes represented by 'node' to the source.  'parentStack' is the
            /// stack that represents the parent of 'node' (thus the top node is Invalid, 
            /// which represents the empty stack)
            /// </summary>
            private void AddToSource(MemoryNode node, StackSourceCallStackIndex parentStack)
            {
                if (node.Children != null)
                {
                    // At the topmost level we have group (UseType), for the node
                    if (parentStack == StackSourceCallStackIndex.Invalid)
                        parentStack = AddFrame("Group " + node.UseType.ToString(), parentStack);

                    if (node.Details.Length != 0)
                    {
                        // Group directories together.  
                        if (node.UseType == UseType.Image || node.UseType == UseType.MappedFile)
                            parentStack = AddDirPathNodes(node.Details, parentStack, false, node.UseType);
                        else
                            parentStack = AddFrame(node.Details, parentStack);
                    }

                    foreach (var child in node.Children)
                        AddToSource(child, parentStack);
                    return;
                }

                var details = node.Details;
                if (node.UseType == UseType.Image && details.Length != 0)
                {
                    details = "Image Section " + details;
                }

                if (details.Length == 0)
                    details = node.Type;

                var frameName = string.Format("{0,-20} address 0x{1:x} size 0x{2:x}", details, node.Address, node.Size);
                StackSourceCallStackIndex nodeStack = AddFrame(frameName, parentStack);

                if (node.PrivateWS != 0)
                    AddSample("Block Private", node.PrivateWS, nodeStack);

                if (node.ShareableWS != 0)
                    AddSample("Block Sharable", node.ShareableWS, nodeStack);
            }
            /// <summary>
            /// Adds nodes for each parent directory that has more than one 'child' (its count is different than it child) 
            /// </summary>
            private StackSourceCallStackIndex AddDirPathNodes(string path, StackSourceCallStackIndex parentStack, bool isDir, UseType useType)
            {
                var lastBackslashIdx = path.LastIndexOf('\\');
                if (lastBackslashIdx >= 0)
                {
                    var dir = path.Substring(0, lastBackslashIdx);
                    parentStack = AddDirPathNodes(dir, parentStack, true, useType);
                }

                var kindName = (useType == UseType.MappedFile) ? "MappedFile" : "Image";
                var prefix = isDir ? kindName + "s in" : kindName;
                return AddFrame(prefix + " " + path, parentStack);
            }
            private void AddSample(string memoryKind, ulong metric, StackSourceCallStackIndex parentStack)
            {
                m_sample.Metric = metric / 1024F;
                var frameName = string.Format("{0,-15} {1}K WS", memoryKind, metric / 1024);
                m_sample.StackIndex = AddFrame(frameName, parentStack);
                AddSample(m_sample);
            }
            private StackSourceCallStackIndex AddFrame(string frameName, StackSourceCallStackIndex parentStack)
            {
                var moduleIdx = Interner.ModuleIntern("");
                var frameIdx = Interner.FrameIntern(frameName, moduleIdx);
                return Interner.CallStackIntern(frameIdx, parentStack);
            }
            StackSourceSample m_sample;

        }
        #endregion
    }

    class PdbScopePerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return ".NET Native Size Graph"; } }
        public override string[] FileExtensions { get { return new string[] { ".imageSize.xml", ".pdb.xml" }; } }   // TODO remove pdb.xml after 1/2015

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            Graph graph = new PdbScopeMemoryGraph(FilePath);

            log.WriteLine(
                   "Opened Graph {0} Bytes: {1:f3}M NumObjects: {2:f3}K  NumRefs: {3:f3}K Types: {4:f3}K RepresentationSize: {5:f1}M",
                   FilePath, graph.TotalSize / 1000000.0, (int)graph.NodeIndexLimit / 1000.0,
                   graph.TotalNumberOfReferences / 1000.0, (int)graph.NodeTypeIndexLimit / 1000.0,
                   graph.SizeOfGraphDescription() / 1000000.0);

            log.WriteLine("Type Histograph > 1% of heap size");
            log.Write(graph.HistogramByTypeXml(graph.TotalSize / 100));

#if false // TODO FIX NOW remove
            using (StreamWriter writer = File.CreateText(Path.ChangeExtension(this.FilePath, ".Clrprof.xml")))
            {
                ((MemoryGraph)graph).DumpNormalized(writer);
            }
#endif
            var ret = new MemoryGraphStackSource(graph, log);
            return ret;
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.ScalingPolicy = ScalingPolicyKind.TimeMetric;
            stackWindow.IsMemoryWindow = true;
            stackWindow.RemoveColumn("WhichColumn");
            stackWindow.GroupRegExTextBox.Items.Add("[group PDBKinds]          #{%}#->type $1");
            stackWindow.GroupRegExTextBox.Items.Add("[raw group modules]       {%}!->module $1");

            stackWindow.FoldRegExTextBox.Items.Add("^RUNTIME_DATA");
            stackWindow.FoldRegExTextBox.Items.Add("^RUNTIME_DATA;^Section;^Image Header");
        }
    }

    class ClrProfilerHeapPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "CLR Profiler Heap"; } }
        public override string[] FileExtensions { get { return new string[] { ".gcheap", ".clrprofiler" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            Graph graph = new ClrProfilerMemoryGraph(FilePath);

            // TODO FIX NOW var refGraph = new Experimental.RefGraph(graph);

            log.WriteLine(
                   "Opened Graph {0} Bytes: {1:f3}M NumObjects: {2:f3}K  NumRefs: {3:f3}K Types: {4:f3}K RepresentationSize: {5:f1}M",
                   FilePath, graph.TotalSize / 1000000.0, (int)graph.NodeIndexLimit / 1000.0,
                   graph.TotalNumberOfReferences / 1000.0, (int)graph.NodeTypeIndexLimit / 1000.0,
                   graph.SizeOfGraphDescription() / 1000000.0);

            log.WriteLine("Type Histograph > 1% of heap size");
            log.Write(graph.HistogramByTypeXml(graph.TotalSize / 100));

#if false // TODO FIX NOW remove
            using (StreamWriter writer = File.CreateText(Path.ChangeExtension(this.FilePath, ".Clrprof.xml")))
            {
                ((MemoryGraph)graph).DumpNormalized(writer);
            }
#endif
            var ret = new MemoryGraphStackSource(graph, log);
            return ret;
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            ConfigureAsMemoryWindow(stackSourceName, stackWindow);
        }
    }

    class HeapDumpPerfViewFile : PerfViewFile
    {
        internal const string Gen0WalkableObjectsViewName = "Gen 0 Walkable Objects";
        internal const string Gen1WalkableObjectsViewName = "Gen 1 Walkable Objects";

        public override string FormatName { get { return "CLR Heap Dump"; } }
        public override string[] FileExtensions { get { return new string[] { ".gcDump", ".gcDump.xml" }; } }

        public const string DiagSessionIdentity = "Microsoft.Diagnostics.GcDump";

        public override string DefaultStackSourceName { get { return "Heap"; } }

        public GCHeapDump GCDump { get { return m_gcDump; } }

        protected internal override StackSource OpenStackSourceImpl(string streamName, TextWriter log, double startRelativeMSec, double endRelativeMSec, Predicate<TraceEvent> predicate)
        {
            OpenDump(log);

            Graph graph = m_gcDump.MemoryGraph;
            GCHeapDump gcDump = m_gcDump;

#if false  // TODO FIX NOW remove
            using (StreamWriter writer = File.CreateText(Path.ChangeExtension(this.FilePath, ".heapDump.xml")))
            {
                ((MemoryGraph)graph).DumpNormalized(writer);
            }
#endif
            int gen = -1;
            if (streamName == Gen0WalkableObjectsViewName)
            {
                Debug.Assert(m_gcDump.DotNetHeapInfo != null);
                gen = 0;
            }
            else if (streamName == Gen1WalkableObjectsViewName)
            {
                Debug.Assert(m_gcDump.DotNetHeapInfo != null);
                gen = 1;
            }

            var ret = GenerationAwareMemoryGraphBuilder.CreateStackSource(m_gcDump, log, gen);

#if false // TODO FIX NOW: support post collection filtering?   
            // Set the sampling ratio so that the number of objects does not get too far out of control.  
            if (2000000 <= (int)graph.NodeIndexLimit)
            {
                ret.SamplingRate = ((int)graph.NodeIndexLimit / 1000000);
                log.WriteLine("Setting the sampling rate to {0}.", ret.SamplingRate);
                MessageBox.Show("The graph has more than 2M Objects.  " +
                    "The sampling rate has been set " + ret.SamplingRate.ToString() + " to keep the GUI responsive.");
            }
#endif
            m_extraTopStats = "";

            double unreachableMemory;
            double totalMemory;
            ComputeUnreachableMemory(ret, out unreachableMemory, out totalMemory);

            if (unreachableMemory != 0)
                m_extraTopStats += string.Format(" Unreachable Memory: {0:n3}MB ({1:f1}%)",
                    unreachableMemory / 1000000.0, unreachableMemory * 100.0 / totalMemory);

            if (gcDump.CountMultipliersByType != null)
            {
                m_extraTopStats += string.Format(" Heap Sampled: Mean Count Multiplier {0:f2} Mean Size Multiplier {1:f2}",
                        gcDump.AverageCountMultiplier, gcDump.AverageSizeMultiplier);
            }

            log.WriteLine("Type Histograph > 1% of heap size");
            log.Write(graph.HistogramByTypeXml(graph.TotalSize / 100));
            return ret;
        }

        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            if (AppLog.InternalUser)
            {
                OpenDump(worker.LogWriter);

                var advanced = new PerfViewTreeGroup("Advanced Group");

                m_Children = new List<PerfViewTreeItem>(2);

                var defaultSource = new PerfViewStackSource(this, DefaultStackSourceName);
                defaultSource.IsSelected = true;
                m_Children.Add(defaultSource);

                if (m_gcDump.InteropInfo != null)
                {
                    // TODO FIX NOW.   This seems to be broken right now  hiding it for now.  
                    // advanced.Children.Add(new HeapDumpInteropObjects(this));
                }

                if (m_gcDump.DotNetHeapInfo != null)
                {
                    advanced.Children.Add(new PerfViewStackSource(this, Gen0WalkableObjectsViewName));
                    advanced.Children.Add(new PerfViewStackSource(this, Gen1WalkableObjectsViewName));
                }

                if (advanced.Children.Count > 0)
                    m_Children.Add(advanced);

                return null;
            }
            return delegate (Action doAfter)
            {
                // By default we have a singleton source (which we dont show on the GUI) and we immediately open it
                m_singletonStackSource = new PerfViewStackSource(this, "");
                m_singletonStackSource.Open(parentWindow, worker);
                if (doAfter != null)
                    doAfter();
            };
        }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            ConfigureAsMemoryWindow(stackSourceName, stackWindow);
            stackWindow.ExtraTopStats = m_extraTopStats;

            if (stackSourceName.Equals(Gen0WalkableObjectsViewName) || stackSourceName.Equals(Gen1WalkableObjectsViewName))
            {
                stackWindow.CallTreeTab.IsSelected = true;      // start with the call tree view
            }
        }

        #region private

        protected internal void OpenDump(TextWriter log)
        {
            if (m_gcDump == null)
            {
                // TODO this is kind of backwards.   The super class should not know about the subclasses.  
                var asSnapshot = this as PerfViewHeapSnapshot;
                if (asSnapshot != null)
                {
                    DotNetHeapInfo dotNetHeapInfo = null;
                    var etlFile = FilePath;
                    ETLPerfViewData.UnZipIfNecessary(ref etlFile, log);

                    MemoryGraph memoryGraph = null;
                    if (asSnapshot.Kind == "JS")
                    {
                        var dumper = new JavaScriptDumpGraphReader(log);
                        memoryGraph = dumper.Read(etlFile, asSnapshot.m_processId, asSnapshot.m_timeRelativeMSec);
                    }
                    else if (asSnapshot.Kind == ".NET")
                    {
                        var dumper = new DotNetHeapDumpGraphReader(log);
                        dumper.DotNetHeapInfo = dotNetHeapInfo = new DotNetHeapInfo();
                        memoryGraph = dumper.Read(etlFile, asSnapshot.m_processId.ToString(), asSnapshot.m_timeRelativeMSec);
                        var resolver = new TypeNameSymbolResolver(FilePath, log);
                        memoryGraph.ResolveTypeName = resolver.ResolveTypeName;
                    }

                    if (memoryGraph == null)
                    {
                        log.WriteLine("Error Unknown dump kind {0} found, ", asSnapshot.Kind);
                        return;
                    }
                    m_gcDump = new GCHeapDump(memoryGraph);
                    m_gcDump.DotNetHeapInfo = dotNetHeapInfo;
                }
                else
                {

                    if (FilePath.EndsWith(".gcDump.xml", StringComparison.OrdinalIgnoreCase))
                        m_gcDump = XmlGcHeapDump.ReadGCHeapDumpFromXml(FilePath);
                    else
                    {
                        m_gcDump = new GCHeapDump(FilePath);

                        // set it up so we resolve any types 
                        var resolver = new TypeNameSymbolResolver(FilePath, log);
                        m_gcDump.MemoryGraph.ResolveTypeName = resolver.ResolveTypeName;
                    }
                }


                if (m_gcDump.TimeCollected.Ticks != 0)
                    log.WriteLine("GCDump collected on {0}", m_gcDump.TimeCollected);
                else
                    log.WriteLine("GCDump collected from a DMP file no time/machine/process info");

                if (m_gcDump.MachineName != null)
                    log.WriteLine("GCDump collected on Machine {0}", m_gcDump.MachineName);
                if (m_gcDump.ProcessName != null)
                    log.WriteLine("GCDump collected on Process {0} ({1})", m_gcDump.MachineName, m_gcDump.ProcessName, m_gcDump.ProcessID);
                if (m_gcDump.TotalProcessCommit != 0)
                    log.WriteLine("Total Process CommitSize {0:n1} MB Working Set {1:n1} MB", m_gcDump.TotalProcessCommit / 1000000.0, m_gcDump.TotalProcessWorkingSet / 1000000.0);

                if (m_gcDump.CollectionLog != null)
                {
                    log.WriteLine("******************** START OF LOG FILE FROM TIME OF COLLECTION **********************");
                    log.Write(m_gcDump.CollectionLog);
                    log.WriteLine("********************  END OF LOG FILE FROM TIME OF COLLECTION  **********************");
                }

#if false // TODO FIX NOW REMOVE
                using (StreamWriter writer = File.CreateText(Path.ChangeExtension(FilePath, ".rawGraph.xml")))
                {
                    m_gcDump.MemoryGraph.WriteXml(writer);
                }
#endif
            }

            MemoryGraph graph = m_gcDump.MemoryGraph;
            log.WriteLine(
                   "Opened Graph {0} Bytes: {1:f3}M NumObjects: {2:f3}K  NumRefs: {3:f3}K Types: {4:f3}K RepresentationSize: {5:f1}M",
                   FilePath, graph.TotalSize / 1000000.0, (int)graph.NodeIndexLimit / 1000.0,
                   graph.TotalNumberOfReferences / 1000.0, (int)graph.NodeTypeIndexLimit / 1000.0,
                   graph.SizeOfGraphDescription() / 1000000.0);
        }

        /// <summary>
        /// These hold stacks which we know they either have an '[not reachable from roots]' or not
        /// </summary>
        private struct UnreachableCacheEntry
        {
            public StackSourceCallStackIndex stack;
            public bool unreachable;
            public bool valid;
        };

        /// <summary>
        /// Returns true if 'stackIdx' is reachable from the roots (that is, it does not have '[not reachable from roots]' as one
        /// of its parent nodes.    'cache' is simply an array used to speed up this process because it remembers the answers for
        /// nodes up the stack that are likely to be used for the next index.   
        /// </summary>
        private static bool IsUnreachable(StackSource memoryStackSource, StackSourceCallStackIndex stackIdx, UnreachableCacheEntry[] cache, int depth)
        {
            if (stackIdx == StackSourceCallStackIndex.Invalid)
                return false;

            int entryIdx = ((int)stackIdx) % cache.Length;
            UnreachableCacheEntry entry = cache[entryIdx];
            if (stackIdx != entry.stack || !entry.valid)
            {
                var callerIdx = memoryStackSource.GetCallerIndex(stackIdx);
                if (callerIdx == StackSourceCallStackIndex.Invalid)
                {
                    var frameIdx = memoryStackSource.GetFrameIndex(stackIdx);
                    var name = memoryStackSource.GetFrameName(frameIdx, false);
                    entry.unreachable = string.Compare(name, "[not reachable from roots]", StringComparison.OrdinalIgnoreCase) == 0;
                }
                else
                    entry.unreachable = IsUnreachable(memoryStackSource, callerIdx, cache, depth + 1);

                entry.stack = stackIdx;
                entry.valid = true;
                cache[entryIdx] = entry;
            }
            return entry.unreachable;
        }

        private static void ComputeUnreachableMemory(StackSource memoryStackSource, out double unreachableMemoryRet, out double totalMemoryRet)
        {
            double unreachableMemory = 0;
            double totalMemory = 0;

            var cache = new UnreachableCacheEntry[10000];
            memoryStackSource.ForEach(delegate (StackSourceSample sample)
            {
                totalMemory += sample.Metric;
                if (IsUnreachable(memoryStackSource, sample.StackIndex, cache, 0))
                    unreachableMemory += sample.Metric;
            });

            unreachableMemoryRet = unreachableMemory;
            totalMemoryRet = totalMemory;
        }

        internal protected GCHeapDump m_gcDump;
        string m_extraTopStats;
        #endregion
    }

    public partial class LinuxPerfViewData : PerfViewFile
    {
        private string[] PerfScriptStreams = new string[]
        {
            "CPU",
            "Thread Time (experimental)"
        };

        public override string FormatName { get { return "LTTng"; } }

        public override string[] FileExtensions { get { return new string[] { ".lttng.zip", ".trace.zip" }; } }

        protected internal override EventSource OpenEventSourceImpl(TextWriter log)
        {
            var traceLog = GetTraceLog(log);
            return new ETWEventSource(traceLog);
        }
        protected internal override StackSource OpenStackSourceImpl(string streamName, TextWriter log, double startRelativeMSec = 0, double endRelativeMSec = double.PositiveInfinity, Predicate<TraceEvent> predicate = null)
        {
            if (PerfScriptStreams.Contains(streamName))
            {
                string xmlPath;
                bool doThreadTime = false;

                if (streamName == "Thread Time (experimental)")
                {
                    xmlPath = CacheFiles.FindFile(this.FilePath, ".perfscript.threadtime.xml.zip");
                    doThreadTime = true;
                }
                else
                {
                    xmlPath = CacheFiles.FindFile(this.FilePath, ".perfscript.cpu.xml.zip");
                }

                if (!CacheFiles.UpToDate(xmlPath, this.FilePath))
                {
                    XmlStackSourceWriter.WriteStackViewAsZippedXml(
                        new ParallelLinuxPerfScriptStackSource(this.FilePath, doThreadTime), xmlPath);
                }

                return new XmlStackSource(xmlPath);
            }

            return null;
        }

        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            // Open the file.
            m_traceLog = GetTraceLog(worker.LogWriter);

            bool hasGC = false;
            bool hasJIT = false;
            if (m_traceLog != null)
            {
                foreach (TraceEventCounts eventStats in m_traceLog.Stats)
                {
                    if (eventStats.EventName.StartsWith("GC/Start"))
                        hasGC = true;
                    else if (eventStats.EventName.StartsWith("Method/JittingStarted"))
                        hasJIT = true;
                }
            }

            m_Children = new List<PerfViewTreeItem>();
            var advanced = new PerfViewTreeGroup("Advanced Group");
            var memory = new PerfViewTreeGroup("Memory Group");

            m_Children.Add(new PerfViewStackSource(this, "CPU"));
            if (AppLog.InternalUser)
                advanced.AddChild(new PerfViewStackSource(this, "Thread Time (experimental)"));

            if (m_traceLog != null)
            {
                m_Children.Add(new PerfViewEventSource(this));
                m_Children.Add(new PerfViewEventStats(this));

                if (hasGC)
                    memory.AddChild(new PerfViewGCStats(this));

                if (hasJIT)
                    advanced.AddChild(new PerfViewJitStats(this));
            }

            if (memory.Children.Count > 0)
                m_Children.Add(memory);

            if (advanced.Children.Count > 0)
                m_Children.Add(advanced);

            return null;
        }

        public override void Close()
        {
            if (m_traceLog != null)
            {
                m_traceLog.Dispose();
                m_traceLog = null;
            }
            base.Close();
        }

        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FileBitmapImage"] as ImageSource; } }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            stackWindow.ScalingPolicy = ScalingPolicyKind.TimeMetric;
            stackWindow.GroupRegExTextBox.Text = stackWindow.GetDefaultGroupPat();
        }

        public TraceLog GetTraceLog(TextWriter log)
        {
            if (m_traceLog != null)
            {
                if (IsUpToDate)
                    return m_traceLog;
                m_traceLog.Dispose();
                m_traceLog = null;
            }
            else if (m_noTraceLogInfo)
                return null;

            var dataFileName = FilePath;
            var options = new TraceLogOptions();
            options.ConversionLog = log;
            if (App.CommandLineArgs.KeepAllEvents)
                options.KeepAllEvents = true;
            options.MaxEventCount = App.CommandLineArgs.MaxEventCount;
            options.SkipMSec = App.CommandLineArgs.SkipMSec;
            //options.OnLostEvents = onLostEvents;
            options.LocalSymbolsOnly = false;
            options.ShouldResolveSymbols = delegate (string moduleFilePath) { return false; };       // Don't resolve any symbols

            // Generate the etlx file path / name.
            string etlxFile = CacheFiles.FindFile(dataFileName, ".etlx");
            if (!File.Exists(etlxFile) || File.GetLastWriteTimeUtc(etlxFile) < File.GetLastWriteTimeUtc(dataFileName))
            {
                FileUtilities.ForceDelete(etlxFile);
                log.WriteLine("Creating ETLX file {0} from {1}", etlxFile, dataFileName);
                try
                {
                    TraceLog.CreateFromLttngTextDataFile(dataFileName, etlxFile, options);
                }
                catch (Exception e)        // Throws this if there is no CTF Information
                {
                    if (e is EndOfStreamException)
                        log.WriteLine("Warning: Trying to open CTF stream failed, no CTF (lttng) information");
                    else
                    {
                        log.WriteLine("Error: Exception CTF conversion: {0}", e.ToString());
                        log.WriteLine("[Error: exception while opening CTF (lttng) data.]");
                    }

                    Debug.Assert(m_traceLog == null);
                    m_noTraceLogInfo = true;
                    return m_traceLog;
                }
            }

            var dataFileSize = "Unknown";
            if (File.Exists(dataFileName))
                dataFileSize = ((new System.IO.FileInfo(dataFileName)).Length / 1000000.0).ToString("n3") + " MB";
            log.WriteLine("ETL Size {0} ETLX Size {1:n3} MB", dataFileSize, (new System.IO.FileInfo(etlxFile)).Length / 1000000.0);

            // Open the ETLX file.  
            m_traceLog = new TraceLog(etlxFile);
            m_utcLastWriteAtOpen = File.GetLastWriteTimeUtc(FilePath);
            if (App.CommandLineArgs.UnsafePDBMatch)
                m_traceLog.CodeAddresses.UnsafePDBMatching = true;
            if (m_traceLog.Truncated)   // Warn about truncation.  
            {
                GuiApp.MainWindow.Dispatcher.BeginInvoke((Action)delegate ()
                {
                    MessageBox.Show("The ETL file was too big to convert and was truncated.\r\nSee log for details", "Log File Truncated", MessageBoxButton.OK);
                });
            }
            return m_traceLog;
        }

        #region Private
        TraceLog m_traceLog;
        bool m_noTraceLogInfo;
        #endregion
    }

    /// <summary>
    /// A simple helper class that looks up symbols for Project N GCDumps 
    /// </summary>
    class TypeNameSymbolResolver
    {
        public enum TypeNameOptions
        {
            None,
            StripModuleName,
        }

        /// <summary>
        /// Create a new symbol resolver.  You give it a context file path (PDBS are looked up next to this if non-null) and
        /// a text writer in which to write symbol diagnostic messages.  
        /// </summary>
        public TypeNameSymbolResolver(string contextFilePath, TextWriter log) { m_contextFilePath = contextFilePath; m_log = log; }

        public string ResolveTypeName(int rvaOfType, TraceModuleFile module, TypeNameOptions options = TypeNameOptions.None)
        {
            Module mod = new Module(module.ImageBase);
            mod.BuildTime = module.BuildTime;
            mod.Path = module.FilePath;
            mod.PdbAge = module.PdbAge;
            mod.PdbGuid = module.PdbSignature;
            mod.PdbName = module.PdbName;
            mod.Size = module.ImageSize;

            string typeName = ResolveTypeName(rvaOfType, mod);

            // Trim the module from the type name if requested.
            if (options == TypeNameOptions.StripModuleName && !string.IsNullOrEmpty(typeName))
            {
                // Strip off the module name if present.
                string[] typeNameParts = typeName.Split(new char[] { '!' }, 2);
                if (typeNameParts.Length == 2)
                {
                    typeName = typeNameParts[1];
                }
            }

            return typeName;
        }

        public string ResolveTypeName(int typeID, Graphs.Module module)
        {
            if (module == null || module.Path == null)
            {
                m_log.WriteLine("Error: null module looking up typeID  0x{0:x}", typeID);
                return null;
            }
            if (module.PdbName == null || module.PdbGuid == Guid.Empty)
            {
                m_log.WriteLine("Error: module for typeID 0x{0:x} {1} does not have PDB signature info.", typeID, module.Path);
                return null;
            }
            if (module.PdbGuid == m_badPdb && m_badPdb != Guid.Empty)
                return null;
            if (m_pdbLookupFailures != null && m_pdbLookupFailures.ContainsKey(module.PdbGuid))  // TODO we are assuming unique PDB names (at least for failures). 
                return null;

            // We check the PDB age and GUID as a proxy for comparing the module itself.
            // This is because the module is per-process, and in a trace where there are many
            // processes of the same application, we end up creating many symbol modules which seems
            // to create memory leaks and takes a very long time to resolve symbols.
            if (!(m_lastModule != null && module != null && m_lastModule.PdbGuid == module.PdbGuid && m_lastModule.PdbAge == module.PdbAge))
            {
                m_lastModule = module;
                m_lastSymModule = null;

                if (m_symReader == null)
                    m_symReader = App.GetSymbolReader(m_contextFilePath);

                m_log.WriteLine("TYPE LOOKUP: Looking up PDB for Module {0}", module.Path);
                var pdbPath = m_symReader.FindSymbolFilePath(module.PdbName, module.PdbGuid, module.PdbAge, module.Path);
                if (pdbPath != null)
                    m_lastSymModule = m_symReader.OpenSymbolFile(pdbPath);
                else
                {
                    if (m_pdbLookupFailures == null)
                        m_pdbLookupFailures = new Dictionary<Guid, bool>();
                    m_pdbLookupFailures.Add(module.PdbGuid, true);
                }
            }
            if (m_lastSymModule == null)
            {
                m_numFailures++;
                if (m_numFailures <= 5)
                {
                    if (m_numFailures == 1 && !Path.GetFileName(module.Path).StartsWith("mrt", StringComparison.OrdinalIgnoreCase))
                    {
                        GuiApp.MainWindow.Dispatcher.BeginInvoke((Action)delegate ()
                        {
                            MessageBox.Show(GuiApp.MainWindow,
                                "Warning: Could not find PDB for module " + Path.GetFileName(module.Path) + "\r\n" +
                                "Some types will not have symbolic names.\r\n" +
                                "See log for more details.\r\n" +
                                "Fix by placing PDB on symbol path or in a directory called 'symbols' beside .gcdump file.",
                                "PDB lookup failure");
                        });
                    }
                    m_log.WriteLine("Failed to find PDB for module {0} to look up type 0x{1:x}", module.Path, typeID);
                    if (m_numFailures == 5)
                        m_log.WriteLine("Discontinuing PDB module lookup messages");
                }
                return null;
            }

            string typeName;
            try
            {
                typeName = m_lastSymModule.FindNameForRva((uint)typeID);
            }
            catch (OutOfMemoryException)
            {
                // TODO find out why this happens?   I think this is because we try to do a ReadRVA 
                m_log.WriteLine("Error: Caught out of memory exception on file " + m_lastSymModule.SymbolFilePath + ".   Skipping.");
                m_badPdb = module.PdbGuid;
                return null;
            }

            typeName = typeName.Replace(@"::`vftable'", "");
            typeName = typeName.Replace(@"::", ".");
            typeName = typeName.Replace(@"EEType__", "");
            typeName = typeName.Replace(@".Boxed_", ".");

            return typeName;
        }

        #region private 
        TextWriter m_log;
        string m_contextFilePath;
        SymbolReader m_symReader;
        SymbolModule m_lastSymModule;
        Graphs.Module m_lastModule;
        int m_numFailures;
        Guid m_badPdb;        // If we hit a bad PDB remember it to avoid logging too much 
        Dictionary<Guid, bool> m_pdbLookupFailures;
        #endregion
    }

    class ClrProfilerCodeSizePerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Clr Profiler Code Size"; } }
        public override string[] FileExtensions { get { return new string[] { ".codesize" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            var codeSizeSource = new ClrProfiler.ClrProfilerMethodSizeStackSource(FilePath);
            log.WriteLine("Info Read:  method called:{0}  totalILSize called:{1}  totalCalls:{2}",
                    codeSizeSource.TotalMethodCount, codeSizeSource.TotalMethodSize, codeSizeSource.TotalCalls);
            return codeSizeSource;
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            var defaultGroup = "[group framework] !System.=> CLR;!Microsoft.=>CLR";
            stackWindow.GroupRegExTextBox.Text = defaultGroup;
            stackWindow.GroupRegExTextBox.Items.Insert(0, defaultGroup);
        }
    }

    class ClrProfilerAllocStacksPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Clr Profiler Alloc"; } }
        public override string[] FileExtensions { get { return new string[] { ".allocStacks" }; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            return new ClrProfiler.ClrProfilerAllocStackSource(FilePath);
        }
        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            var defaultGroup = "[group framework] !System.=> CLR;!Microsoft.=>CLR";
            stackWindow.GroupRegExTextBox.Text = defaultGroup;
            stackWindow.GroupRegExTextBox.Items.Insert(0, defaultGroup);
        }
    }

    public class ProcessDumpPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Process Dump"; } }
        public override string[] FileExtensions { get { return new string[] { ".dmp" }; } }

        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            App.CommandLineArgs.ProcessDumpFile = FilePath;
            GuiApp.MainWindow.TakeHeapShapshot(null);
        }
        public override void Close() { }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FileBitmapImage"] as ImageSource; } }

        public const string DiagSessionIdentity = "Microsoft.Diagnostics.Minidump";
    }

    public class ScenarioSetPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Scenario Set"; } }
        public override string[] FileExtensions { get { return new string[] { ".scenarioSet.xml" }; } }

        public override string HelpAnchor { get { return "ViewingMultipleScenarios"; } }

        protected internal override StackSource OpenStackSourceImpl(TextWriter log)
        {
            Dictionary<string, string> pathDict = null;
            XmlReaderSettings settings = new XmlReaderSettings() { IgnoreWhitespace = true, IgnoreComments = true };
            using (Stream dataStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                using (XmlReader reader = XmlTextReader.Create(dataStream, settings))
                {
                    pathDict = DeserializeConfig(reader, log);
                }
            }

            if (pathDict.Count == 0)
                throw new ApplicationException("No scenarios found");

            // Open XmlStackSources on each of our paths.
            var sources = pathDict.Select(
                (pair, idx) =>
                {
                    string name = pair.Key, path = pair.Value;
                    log.WriteLine("[Opening [{0}/{1}] {2} ({3})]", idx + 1, pathDict.Count, name, path);
                    var source = new XmlStackSource(path);
                    return new KeyValuePair<string, StackSource>(name, source);
                }
            ).ToList(); // Copy to list to prevent repeated enumeration from having an effect.

            return new AggregateStackSource(sources);
        }

        protected internal override void ConfigureStackWindow(string stackSourceName, StackWindow stackWindow)
        {
            ConfigureAsEtwStackWindow(stackWindow, false, false);
        }

        #region private

        /// <summary>
        /// Search for scenario data files matching a pattern, and add them to a dictionary.
        /// </summary>
        /// <param name="filePattern">The wildcard file pattern to match. Must not be null.</param>
        /// <param name="namePattern">The pattern by which to name scenarios. If null, defaults to "scenario $1".</param>
        /// <param name="includePattern">If non-null, a pattern which must be matched for the scenario to be added</param>
        /// <param name="excludePattern">If non-null, a pattern which if matched causes the scenario to be excluded</param>
        /// <param name="dict">The dictionary to which to add the scenarios found.</param>
        /// <param name="log">A log file to write log messages.</param>
        /// <param name="baseDir">
        /// The directory used to resolve relative paths.
        /// Defaults to the directory of the XML file represented by this ScenarioSetPerfViewFile.
        /// </param>
        private void AddScenariosToDictionary(
            string filePattern, string namePattern, string includePattern, string excludePattern,
            Dictionary<string, string> dict, TextWriter log,
            string baseDir = null)
        {
            Debug.Assert(filePattern != null);

            if (baseDir == null)
                baseDir = Path.GetDirectoryName(FilePath);

            if (namePattern == null)
                namePattern = "scenario $1";

            string replacePattern = Regex.Escape(filePattern)
                                    .Replace(@"\*", @"([^\\]*)")
                                    .Replace(@"\?", @"[^\\]");

            if (!(filePattern.EndsWith(".perfView.xml", StringComparison.OrdinalIgnoreCase) ||
                  filePattern.EndsWith(".perfView.xml.zip", StringComparison.OrdinalIgnoreCase)))
                throw new ApplicationException("Files must be PerfView XML files");

            string pattern = Path.GetFileName(filePattern);
            string dir = Path.GetDirectoryName(filePattern);

            // Tack on the base directory if we're not already an absolute path.
            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(baseDir, dir);

            var replaceRegex = new Regex(replacePattern, RegexOptions.IgnoreCase);
            var defaultRegex = new Regex(@"(.*)", RegexOptions.IgnoreCase);

            // TODO: Directory.GetFile
            foreach (string file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
            {
                // Filter out those that don't match the include pattern 
                if (includePattern != null && !Regex.IsMatch(file, includePattern))
                    continue;
                // or do match the exclude pattern.  
                if (excludePattern != null && Regex.IsMatch(file, excludePattern))
                    continue;

                string name = null;
                if (namePattern != null)
                {
                    var match = replaceRegex.Match(file);

                    // We won't have a group to match if there were no wildcards in the pattern.
                    if (match.Groups.Count < 1)
                        match = defaultRegex.Match(GetFileNameWithoutExtension(file));

                    name = match.Result(namePattern);
                }

                dict[name] = file;

                log.WriteLine("Added '{0}' ({1})", name, file);
            }
        }

        /// <summary>
        /// Deserialize a scenario XML config file.
        /// </summary>
        /// <param name="reader">The XmlReader containing the data to deserialize.</param>
        /// <param name="log">The TextWriter to log output to.</param>
        /// <returns>A Dictionary mapping scenario names to .perfView.xml(.zip) data file paths.</returns>
        /// <remarks>
        /// Scenario XML config files contain a ScenarioSet root element. That element contains
        /// one or more Scenarios elements. A Scenarios element has two attributes: "files" is a required
        /// filename pattern, and namePattern is a pattern by which to name the scenario.
        /// 
        /// files is a required attribute specifying where to find the data files for the scenario(s). Wildcards
        /// are acceptable - any files matched by the wildcard will be added to the scenario set. All paths are
        /// relative to the location of the XML config file.
        /// 
        /// namePattern can contain substitutions as specified in Regex.Replace. Each * in the wildcard
        /// pattern will be converted to an appropriate capturing group. If no wildcards are specified, $1 will be
        /// set to the base name of the data file as specified by <see cref="PerfViewFile.GetFileNameWithoutExtension"/>.
        /// 
        /// files is a required attribute. namePattern is optional, and defaults to "scenario $1".
        /// 
        /// If multiple scenarios have the same name, scenarios later in the file will override scenarios
        /// earlier in the file.
        /// </remarks>
        /// <example>
        /// Example config file:
        /// <ScenarioSet>
        /// <Scenarios files="*.perfView.xml.zip" namePattern="Example scenario [$1]" />
        /// <Scenarios files="foo.perfView.xml.zip" namePattern="Example scenario [baz]" />
        /// </ScenarioSet>
        /// 
        /// Files in the directory:
        /// foo.perfView.xml.zip
        /// bar.perfView.xml.zip
        /// baz.perfView.xml.zip
        /// 
        /// Return value:
        /// "Example scenario [foo]" => "foo.perfView.xml.zip"
        /// "Example scenario [bar]" => "bar.perfView.xml.zip"
        /// "Example scenario [baz]" => "foo.perfView.xml.zip"
        /// </example>
        private Dictionary<string, string> DeserializeConfig(XmlReader reader, TextWriter log)
        {
            var pathDict = new Dictionary<string, string>();

            if (!reader.ReadToDescendant("ScenarioSet"))
                throw new ApplicationException("The file " + FilePath + " does not have a Scenario element");

            if (!reader.ReadToDescendant("Scenarios"))
                throw new ApplicationException("No scenarios specified");

            do
            {
                string filePattern = reader["files"];
                string namePattern = reader["namePattern"];
                string includePattern = reader["includePattern"];
                string excludePattern = reader["excludePattern"];

                if (filePattern == null)
                    throw new ApplicationException("File path is required.");

                AddScenariosToDictionary(filePattern, namePattern, includePattern, excludePattern, pathDict, log);
            }
            while (reader.ReadToNextSibling("Scenarios"));

            return pathDict;
        }

        #endregion
    }

    /// <summary>
    /// Class to represent the Visual Studio .diagsesion file format that is defined
    /// as part of Microsoft.DiagnosticsHub.Packaging
    /// </summary>
    public class DiagSessionPerfViewFile : PerfViewFile
    {
        public override string FormatName { get { return "Diagnostics Session"; } }
        public override string[] FileExtensions { get { return new string[] { ".diagsession" }; } }

        public override IList<PerfViewTreeItem> Children
        {
            get
            {
                if (m_Children == null)
                {
                    m_Children = new List<PerfViewTreeItem>();
                }

                return m_Children;
            }
        }

        public override void Open(Window parentWindow, StatusBar worker, Action doAfter = null)
        {
            if (!m_opened)
            {
                IsExpanded = false;

                worker.StartWork("Opening " + Name, delegate ()
                {
                    OpenImpl(parentWindow, worker);
                    ExecuteOnOpenCommand(worker);

                    worker.EndWork(delegate ()
                    {
                        m_opened = true;

                        FirePropertyChanged("Children");

                        IsExpanded = true;

                        if (doAfter != null)
                        {
                            doAfter();
                        }
                    });
                });
            }
        }

        protected override Action<Action> OpenImpl(Window parentWindow, StatusBar worker)
        {
            worker.Log("Opening diagnostics session file " + Path.GetFileName(FilePath));

            using (DhPackage dhPackage = DhPackage.Open(FilePath))
            {
                // Get all heap dump resources
                AddResourcesAsChildren(worker, dhPackage, HeapDumpPerfViewFile.DiagSessionIdentity, ".gcdump", (localFilePath) =>
                    {
                        return HeapDumpPerfViewFile.Get(localFilePath);
                    });

                // Get all process dump resources
                AddResourcesAsChildren(worker, dhPackage, ProcessDumpPerfViewFile.DiagSessionIdentity, ".dmp", (localFilePath) =>
                    {
                        return ProcessDumpPerfViewFile.Get(localFilePath);
                    });

                // Get all ETL files
                AddResourcesAsChildren(worker, dhPackage, "DiagnosticsHub.Resource.EtlFile", ".etl", (localFilePath) =>
                    {
                        return ETLPerfViewData.Get(localFilePath);
                    });
            }

            return null;
        }

        public override void Close() { }
        public override ImageSource Icon { get { return GuiApp.MainWindow.Resources["FileBitmapImage"] as ImageSource; } }

        /// <summary>
        /// Gets a new local file path for the given resource, extracting it from the .diagsession if required
        /// </summary>
        /// <param name="package">The diagsession package object</param>
        /// <param name="resource">The diagsession resource object</param>
        /// <param name="fileExtension">The final extension to use</param>
        /// <returns>The full local file path to the resource</returns>
        private static string GetLocalFilePath(DhPackage package, ResourceInfo resource, string fileExtension)
        {
            string localFileName = Path.GetFileNameWithoutExtension(resource.Name);
            string localFilePath = CacheFiles.FindFile(localFileName, fileExtension);

            if (!File.Exists(localFilePath))
            {
                package.ExtractResourceToPath(ref resource.ResourceId, localFilePath);
            }

            return localFilePath;
        }

        /// <summary>
        /// Adds child files from resources in the DhPackage
        /// </summary>
        private void AddResourcesAsChildren(StatusBar worker, DhPackage dhPackage, string resourceIdentity, string fileExtension, Func<string/*localFileName*/, PerfViewFile> getPerfViewFile)
        {
            ResourceInfo[] resources;
            dhPackage.GetResourceInformationByType(resourceIdentity, out resources);

            IEnumerable<ResourceInfo> orderedResources = resources
                    .OrderBy(r => DhPackagingExtensions.ToDateTime(r.TimeAddedUTC));

            foreach (var resource in resources)
            {
                Guid resourceId = resource.ResourceId;

                string localFilePath = GetLocalFilePath(dhPackage, resource, fileExtension);

                worker.Log("Found '" + resource.ResourceId + "' resource '" + resource.Name + "'. Loading ...");

                PerfViewFile perfViewFile = getPerfViewFile(localFilePath);

                this.Children.Add(perfViewFile);

                worker.Log("Loaded " + resource.Name + ". Loading ...");
            }
        }
    }
}
