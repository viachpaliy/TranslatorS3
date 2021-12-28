﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Core;
using LogView;
using System.Windows.Threading;
using System.Timers;
using static ScriptEditor.Tag;
using CWindow = ConsoleWindow.ConsoleWindow;
using Executor;
using TranslatorS3.Entities;
using ScriptEditor;
using System.Windows.Media;
using static System.IO.File;

namespace TranslatorS3
{

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly MainWindow mainWindow;

        private Grammar Grammar { get; set; }

        private ITokenParserResult TokenParserResult { get; set; }
        private IParserResult SyntaxParserResult { get; set; }
        private ISemanticParserResult SemanticParserResult { get; set; }
        private IRpnParserResult RpnParserResult { get; set; }

        private IEnumerable<IParsedToken> ParsedTokens => TokenParserResult.ParsedTokens;


        private IExecutor Executor { get; set; }


        private CoolEditor Editor => mainWindow.ScriptEditor;
        private IDocument ActiveDocument => mainWindow.ScriptEditor.ActiveDocument;


        private readonly Timer timer;

        // One second to idle before starting analyzing
        private const int TimeToAnalyze = 1000;

        private bool isTimerAssigned;
        private IDocument timerAssignedDocument;

        private Window logWindow;
        private CWindow consoleWindow;

        //private delegate void AnalysisFinishedEventHandler();
        //private event AnalysisFinishedEventHandler AnalysisFinished;
        private bool isBeingAnalysed;
        private bool isDocumentBeingUpdated;

        #region Initialize

        public App()
        {
            mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Closed += MainWindow_Closed;
            mainWindow.Loaded += MainWindow_Loaded;

            mainWindow.ShowConfigClick += ShowConfiguration_Click;
            mainWindow.ShowLoggerClick += ShowLogger_Click;
            mainWindow.StepOverClick += StepOver_Click;
            mainWindow.RunClick += Run_Click;

            mainWindow.NewFileClick += MainWindow_NewFileClick;
            mainWindow.OpenFileClick += MainWindow_OpenFileClick;
            mainWindow.SaveFileClick += MainWindow_SaveFileClick;

            Editor.DocumentUpdated += Document_Updated;
            Editor.DocumentFocused += Document_Focused;

            timer = new Timer() { AutoReset = false };

            mainWindow.Show();

        }

       

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Configuration.Load();

            InitializeParsers();

            Executor = ExecutorProxy.Create();
            Executor.GrammarNodes = Grammar.Nodes;
            Executor.Started += Executor_Started;
            Executor.Ended += Executor_Ended;
            Executor.Input += Executor_Input;

            OpenDefauldDocument();
        }

        private void InitializeParsers()
        {
            #region Grammar
            Grammar = new Grammar();
            //Grammar.Load();
            var finiteAutomaton = new FiniteAutomaton();

            dynamic parser = ParserManager.InitializeParser(
                "GrammarParser.dll",
                "GrammarParser.GrammarParser",
                Configuration.Path.GrammarInputXml,
                Configuration.Parser.ShouldIncludeTerminalsFromInsideOfDefinedTokens,
                Configuration.Parser.ShouldConvertLeftRecursionToRight);


            Grammar.Parse(parser);

            var controlTerminals = Grammar.Nodes.Terminals.Where(n => n.IsControl).Select(n => n.Name).ToArray();

            Editor.DocumentOpened += (doc) => mainWindow.ScriptEditor.ApplyTextColor(doc, controlTerminals, Color.FromRgb(0, 0, 255));


            // Parse finite automaton

            parser = ParserManager.InitializeParser(
                "FiniteAutomatonParser.dll",
                "FiniteAutomatonParser.FiniteAutomatonParser",
                Grammar.ClassTable,
                Grammar.Nodes,
                Grammar.UnclassifiedTerminals);

            finiteAutomaton.Parse(parser);
            //finiteAutomaton.Save();
            //finiteAutomaton.Load();


            SaveGrammarTxt(Grammar);
            SaveGrammarFactorizedTxt(Grammar);
            //Grammar.Save();

            #endregion


            ParserManager.InitializeParser(
                "SyntaxRecursiveParser.dll",
                "SyntaxRecursiveParser.SyntaxRecursiveParser",
                true,
                Grammar.ClassTable.TokenClasses.Forward(Grammar.ClassTable.UndefinedTokenClassName),
                Grammar.Nodes.Axiom);

            //PushdownAutomaton pushdownAutomaton = new PushdownAutomaton();
            //pushdownAutomaton.Load();


            //ParserManager.InitializeParser(
            //    "SyntaxPushdownParser.dll",
            //    "SyntaxPushdownParser.SyntaxPushdownParser",
            //    pushdownAutomaton,
            //    Grammar.Nodes,
            //    true,
            //    Grammar.ClassTable.TokenClasses.Forward(Grammar.ClassTable.UndefinedTokenClassName));

            ParserManager.InitializeParser(
               "TokenParser.dll",
               "TokenParser.TokenParser",
               Grammar.Nodes,
               Grammar.ClassTable,
               finiteAutomaton);

            ParserManager.InitializeParser(
               "SemanticParser.dll",
               "SemanticParser.SemanticParser",
               Grammar.ClassTable,
               Grammar.Nodes);


            #region Predescence
            //Create predescence table

            //parser = ParserManager.InitializeParser(
            //   "PredescenceTableParser.dll",
            //   "PredescenceTableParser.PredescenceTableParser",
            //   Grammar.Nodes);

            //var predescenceTable = new PredescenceTable();
            //predescenceTable.Parse(parser);

            //var f = (Func<IEnumerable<IParsedToken>>)(() => ParsedTokens);

            //parser = ParserManager.InitializeParser(
            //   "SyntaxPredescenceTableParserWithPOLIZ.dll",
            //   "SyntaxPredescenceTableParser.SyntaxPredescenceTableParser",
            //   predescenceTable.Nodes,
            //   f,
            //   Grammar.Nodes,
            //   Grammar.Nodes.Axiom);

            //parser = ParserManager.InitializeParser(
            //    "SyntaxPredescenceTableParser.dll",
            //    "SyntaxPredescenceTableParser.SyntaxPredescenceTableParser",
            //    predescenceTable.Nodes,
            //    f,
            //    Grammar.Nodes,
            //    Grammar.Nodes.Axiom);

            //SavePredescenceTableTxt(predescenceTable, Grammar);

            #endregion


            ParserManager.InitializeParser(
                "RpnParser.dll",
                "RpnParser.RpnParser",
                Grammar.Nodes,
                Configuration.Path.StatementRulesXml);

        }

        #endregion


        #region File Operations

        private bool TryOpenDocument(string path, out IDocument document)
        {
            if (!Exists(path))
            {
                document = null;
                return false;
            }

            document = new Document(ReadAllText(path))
            {
                Name = Configuration.Path.ScriptTxt.Substring(Configuration.Path.ScriptTxt.LastIndexOf("\\") + 1),
                Path = Configuration.Path.ScriptTxt,
            };

            return true;
        }

        private bool TryOpenAndFocusDocument(string path)
        {
            if (!TryOpenDocument(path, out var document))
            {
                return false;
            }

            mainWindow.ScriptEditor.OpenDocument(document);

            mainWindow.ScriptEditor.Focus(document);

            //Document_Updated(document);

            return true;
        }

        private void OpenEmptyDocument()
        {
            var document = new Document("")
            {
                Name = "noname",
                Path = Configuration.Path.ScriptTxt,
            };

            mainWindow.ScriptEditor.OpenDocument(document);

            mainWindow.ScriptEditor.Focus(document);

            //Document_Updated(document);
        }

        private void OpenDefauldDocument()
        {
            if (!TryOpenAndFocusDocument(Configuration.Path.ScriptTxt))
            {
                OpenEmptyDocument();
            }
        }

        private void SaveDocument(string path, IDocument document)
        {
            var contentWithNoLastLineEnding = document.Text
               .Take(ActiveDocument.Text.Length - 2)
               .ToStr();

            WriteAllText(path, contentWithNoLastLineEnding);
        }

        #endregion


        #region Top bar menu

        private void ShowConfiguration_Click(object sender, RoutedEventArgs e)
        {
            ConfigView.ConfigView configView = new ConfigView.ConfigView();

            configView.ConfigType = typeof(Configuration);

            configView.Initialize();

            Window window = new Window
            {
                Content = configView,
                Height = 350,
                Width = 600,
            };
            window.ShowDialog();
        }

        private void ShowLogger_Click(object sender, RoutedEventArgs e)
        {
            if (logWindow != null)
            {
                if (logWindow.WindowState == WindowState.Minimized)
                {
                    SystemCommands.RestoreWindow(logWindow);
                }

                if (!logWindow.IsActive)
                {
                    logWindow.Activate();
                }

                return;
            }

            logWindow = new Window() { Content = new LogView.LogView(), Title = "Logger" };
            logWindow.Closed += (s, _) => logWindow = null;

            logWindow.Show();
        }

        private async void StepOver_Click(object sender, RoutedEventArgs e)
        {
            if (Executor.State != State.Idle && Executor.State != State.Paused)
            {
                return;
            }

            while (RpnParserResult is null)
            {
                await Task.Delay(10);
            }

            Executor.ExecutionNodes = RpnParserResult.RpnStream;

            _ = Executor.StepOver();
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            while (isBeingAnalysed || isDocumentBeingUpdated)
            {
                MessageBox.Show("Wait until analysis is finished.");
                await Task.Delay(100);
            }

            if (Executor.State != State.Idle && Executor.State != State.Paused)
            {
                return;
            }


            while (RpnParserResult is null)
            {
                await Task.Delay(100);
            }

            Executor.ExecutionNodes = RpnParserResult.RpnStream;

            _ = Executor.Run();
        }

        private void MainWindow_NewFileClick(object sender, RoutedEventArgs e)
        {
            OpenEmptyDocument();
        }

        private void MainWindow_OpenFileClick(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();

            dlg.DefaultExt = ".txt";

            //dlg.Filter = "JPEG Files (*.txt)|*.jpeg|PNG Files (*.png)|*.png|JPG Files (*.jpg)|*.jpg|GIF Files (*.gif)|*.gif";
            dlg.Filter = "Text Files (*.txt)|*.txt";

            bool? result = dlg.ShowDialog();

            if (result ?? false)
            {
                // Open document.
                string filename = dlg.FileName;

                if (!TryOpenAndFocusDocument(filename))
                {
                    MessageBox.Show("Could not open document.");
                }
            }
        }

        private void MainWindow_SaveFileClick(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();

            dlg.DefaultExt = ".txt";

            //dlg.Filter = "JPEG Files (*.txt)|*.jpeg|PNG Files (*.png)|*.png|JPG Files (*.jpg)|*.jpg|GIF Files (*.gif)|*.gif";
            dlg.Filter = "Text Files (*.txt)|*.txt";

            bool? result = dlg.ShowDialog();

            if (result ?? false)
            {
                // Open document.
                string filename = dlg.FileName;

                try
                {
                    SaveDocument(filename, ActiveDocument);

                    var documentName = System.IO.Path.GetFileName(filename);

                    ActiveDocument.GetType().GetProperty("Name").SetValue(ActiveDocument, documentName);
                }
                catch (Exception exception)
                {
                    MessageBox.Show("Could not save document." + exception.Message);
                }
            }
        }

        #endregion



        private void ShowErrors(IDocument document)
        {
            var errors = GetErrors().ToArray();

            var semanticErrors = errors
               .Where(n => n.Tag == "semantic")
               .SelectMany(n => n.TokensOnError.Select(m => (m.InStringPosition, m.InStringPosition + m.Name.Length - 1)))
               .ToArray();

            var syntaxErrors = errors
                .Where(n => n.Tag == "syntax")
                .SelectMany(n => n.TokensOnError.Select(m => (m.InStringPosition, m.InStringPosition + m.Name.Length - 1)))
                .ToArray();

            var lexicalErrors = errors
               .Where(n => n.Tag == "lexical")
               .SelectMany(n => n.TokensOnError.Select(m => (m.InStringPosition, m.InStringPosition + m.Name.Length - 1)))
               .ToArray();

            document.ResetHighlight();
            //document.ResetFormat();

            document.ApplyHighlight(semanticErrors, new[] { Semantic }, Brushes.GreenYellow);

            document.ApplyHighlight(syntaxErrors, new[] { Syntax }, Brushes.OrangeRed);

            document.ApplyHighlight(lexicalErrors, new[] { Lexical }, Brushes.Violet);


            mainWindow.ErrorPanel.ReplaceErrors(document, errors);
        }

        private void Update(IDocument document)
        {
            isBeingAnalysed = true;

            ParserManager.TokenParser.Script = document.Text;
            TokenParserResult = ParserManager.TokenParser.Parse();

            if (ParsedTokens == null)
            {
                mainWindow.ErrorPanel.ReplaceErrors(document, new IParserError[] { });

                return;
            }

            ParserManager.SyntaxParser.ParsedTokens = ParsedTokens;
            SyntaxParserResult = ParserManager.SyntaxParser.Parse();

            ParserManager.SemanticParser.ParsedTokens = ParsedTokens;
            SemanticParserResult = ParserManager.SemanticParser.Parse();


            if ((!SyntaxParserResult.Errors?.Any() ?? true) && (!SemanticParserResult.Errors?.Any() ?? true))
            //if ((SyntaxParserResult.Errors is null || !SyntaxParserResult.Errors.Any()) && (SemanticParserResult.Errors is null || !SemanticParserResult.Errors.Any()))
            {
                ParserManager.RpnParser.ParsedTokens = ParsedTokens;
                ParserManager.RpnParser.RootScope = SemanticParserResult.RootScope;
                RpnParserResult = ParserManager.RpnParser.Parse();
            }

            ShowErrors(document);

            isBeingAnalysed = false;
        }

        private void Document_Updated(IDocument document)
        {
            isDocumentBeingUpdated = true;

            if (!timer.Enabled)
                timer.Start();

            timer.Interval = TimeToAnalyze;

            //if (isTimerAssigned || timerAssignedDocument == document) return;

            //timer.Elapsed += (sender, e) =>
            //{
            //    Dispatcher.Invoke(() => Update(document), DispatcherPriority.Background);
            //};
            //isTimerAssigned = true;
            //timerAssignedDocument = document;

        }

        private void Document_Focused(IDocument document)
        {
            //if (!timer.Enabled)
            //    timer.Start();

            //timer.Interval = TimeToAnalyze;

            timer.Elapsed += (sender, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    Update(document);
                    isDocumentBeingUpdated = false;
                }, DispatcherPriority.Background);
            };
            isTimerAssigned = true;
            timerAssignedDocument = document;

            Document_Updated(document);
            //throw new NotImplementedException();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            //var contentWithNoLastLineEnding = ActiveDocument.Text
            //   .Take(ActiveDocument.Text.Length - 2)
            //   .ToStr();

            //Save(Configuration.Path.ScriptTxt, contentWithNoLastLineEnding);

            if (SyntaxParserResult != null)
            {
                var errors = GetErrors()
                    .GroupBy(n => n.Tag)
                    .Select(n => $"{n.Key}\r\n{string.Join("\r\n", n.Select(m => m.Message))}");

                Save(Configuration.Path.ErrorsTxt, string.Join("\r\n\r\n", errors));
            }

            SaveParsedTokensTxt(TokenParserResult);

            SaveClassTable("Output/classTable.txt", Grammar.ClassTable);
            SaveLexemTable("Output/lexemTable.txt", Grammar.Nodes);

            logWindow?.Close();

            Configuration.Save();
        }

        private IEnumerable<IParserError> GetErrors()
        {
            if (SyntaxParserResult.Errors == null)
                return SemanticParserResult.Errors;

            if (SemanticParserResult.Errors == null)
                return SyntaxParserResult.Errors;

            var errors = SyntaxParserResult.Errors
               .Concat(SemanticParserResult.Errors);

            return errors;
        }




        #region Executor

        private void Executor_Input(Action<object> input)
        {
            consoleWindow.Input(input);
        }

        private void Executor_Ended()
        {
            consoleWindow.Close();
        }

        private void Executor_Started()
        {
            consoleWindow = new CWindow
            {
                Height = 480,
                Width = 640,
            };
            consoleWindow.Show();
            consoleWindow.Closed += ConsoleWindow_Closed;
            Executor.Output = consoleWindow.Output;
        }

        private async void ConsoleWindow_Closed(object sender, EventArgs e)
        {
            await Executor.Abort();
        }

        #endregion

        #region Save

        //private static void SaveFiniteAutomaton(string path, IFiniteAutomaton automaton)
        //{
        //    var states = automaton.States.Select(n => 
        //    {
        //        var links = n.Value.Links.Select(m => $"{m.Key,-20} {m.Value,-20}");
        //        $"{n.Key,-20} {string.Join("",links)}"
        //        })
        //}

        private static void SaveClassTable(string path, IClassTable classTable)
        {
            WriteAllLines(path, classTable.SymbolClasses.Select(n => $"{n.Key,-20} {n.Value,-20}"));
        }

        private static void SaveLexemTable(string path, INodeCollection nodes)
        {


            var ns = nodes.Tokens;

            var str = ns.Select(n => $"{n.Name,-20} {n.Id,-20}");

            WriteAllLines(path, str);
        }

        private static void Save(string path, string contents)
        {
            Configuration.CreateDirectoryFromPath(path);

            WriteAllText(path, contents);
        }

        private static void SaveGrammarFactorizedTxt(Grammar grammar)
        {
            Configuration.CreateDirectoryFromPath(Configuration.Path.GrammarFactorizedTxt);

            var copies = grammar.Nodes.Unsorted;

            var list = copies.Select(n =>
            {
                if (n is IMedium m)
                {
                    return $"{n}::={(n as IFactor).ToString()}";
                }

                if (n is IClass c)
                {
                    return $"{n}::={string.Join("|", c.Symbols.OrderBy(k => k))}";
                }

                return null;
            }).Where(n => !string.IsNullOrWhiteSpace(n));

            WriteAllLines(Configuration.Path.GrammarFactorizedTxt, list);

        }

        private static void SaveGrammarTxt(Grammar grammar)
        {
            Configuration.CreateDirectoryFromPath(Configuration.Path.GrammarTxt);

            var copies = grammar.Nodes.Unsorted;

            var list = copies.Select(n =>
            {
                if (n is IMedium m)
                {
                    return n.ToString() + "::=" + string.Join("|", m.Cases.Select(k => string.Join("", k.Count() == 0 ? new List<string> { "^" } : k.Select(j => j.ToString()))));
                }

                if (n is IClass c)
                {
                    return $"{n}::={string.Join("|", c.Symbols.OrderBy(k => k))}";
                }

                return null;
            }).Where(n => !string.IsNullOrWhiteSpace(n));

            WriteAllLines(Configuration.Path.GrammarTxt, list);

        }

        private static void SavePredescenceTableTxt(PredescenceTable table, Grammar grammar)
        {
            Configuration.CreateDirectoryFromPath(Configuration.Path.PredescenceTableTxt);

            List<string> ignoredNodes = new List<string> { "tail", "head", "digit" };

            var unsorted = grammar.Nodes.Unsorted;

            var sortedNodes = unsorted.OfType<IMedium>().Where(n => !(n is IDefinedToken)).Cast<INode>()
                .Concat(unsorted.OfType<IClass>())
                .Concat(unsorted.OfType<IDefinedToken>().Where(n => !(n is ITerminal)))
                .Concat(unsorted.OfType<ITerminal>())
                .Where(n => !ignoredNodes.Contains(n.Name));



            List<string> content = new List<string>();

            const int firstColumnWidth = 12;

            content.Add(string.Join("", sortedNodes.Select(n => GetColumn(n, n.ToString()))
                .Prepend($"{string.Empty,firstColumnWidth}|")
                .Append("#|")));

            foreach (var verticalNode in sortedNodes)
            {
                if (table.Nodes.ContainsKey(verticalNode.Id))
                {
                    string str = $"{verticalNode.ToString(),firstColumnWidth}|";

                    foreach (var horizontalNode in sortedNodes)
                    {
                        if (table.Nodes[verticalNode.Id].Relashionships.ContainsKey(horizontalNode.Id))
                        {
                            str += GetColumn(horizontalNode, GetSign(verticalNode.Id, horizontalNode.Id));
                        }
                        else
                        {
                            str += GetColumn(horizontalNode, string.Empty);
                        }
                    }

                    // Add each > #
                    str += ">|";


                    content.Add(str);
                }
            }

            // Add # < each
            content.Add(string.Join("", sortedNodes.Select(node => GetColumn(node, "<")).Prepend($"{"#",firstColumnWidth}|")));


            Logger.AddRange("predescenceTable", content);

            WriteAllLines(Configuration.Path.PredescenceTableTxt, content);

            return;

            string GetSign(int verticalId, int horizontalId)
            {
                switch (table.Nodes[verticalId].Relashionships[horizontalId])
                {
                    case Relationship.Undefined:
                        return string.Empty;
                    case Relationship.Greater:
                        return ">";
                    case Relationship.Lower:
                        return "<";
                    case Relationship.Equal:
                        return "=";
                    default:
                        return table.Nodes[verticalId].Relashionships[horizontalId].ToString();
                }
            }

            string GetColumn(INode horizontalNode, string str)
            {
                return string.Format("{0,-" + horizontalNode.ToString().Length + "}|", str);
            }
        }

        private static void SaveParsedTokensTxt(ITokenParserResult tokenParserResult)
        {
            Configuration.CreateDirectoryFromPath(Configuration.Path.ParsedNodesDirectory);

            if (tokenParserResult.ParsedTokens is null || !tokenParserResult.ParsedTokens.Any())
            {
                return;
            }

            var tables = tokenParserResult.ParsedTokens.Distinct(n => n.Name).GroupBy(n => n.TokenClassId);

            IDictionary<string, int> GetTable(int classId)
            {
                return tables.First(m => m.Key == classId).Select((m, j) => (j, m.Name)).ToDictionary(m => m.Name, m => m.j);
            }

            try
            {
                var parsedNodes = tokenParserResult.ParsedTokens.Select((n, i) => $"{i,4} {n.Name,-10} {n.TokenClassId,2} " +
                    $"{GetTable(n.TokenClassId)[n.Name]}");

                var identifiers = GetTable(1).Select(n => $"{n.Value,4} {n.Key}");
                var constants = GetTable(3).Select(n => $"{n.Value,4} {n.Key}");
                var labels = GetTable(2).Select(n => $"{n.Value,4} {n.Key}");


                WriteAllLines(Configuration.Path.ParsedNodesDirectory + "parsed-nodes.txt", parsedNodes);
                WriteAllLines(Configuration.Path.ParsedNodesDirectory + "identifiers.txt", identifiers);
                WriteAllLines(Configuration.Path.ParsedNodesDirectory + "constants.txt", constants);
                WriteAllLines(Configuration.Path.ParsedNodesDirectory + "labels.txt", labels);
            }
            catch { }
        }

        #endregion

    }
}
