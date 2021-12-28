using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;


namespace ScriptEditor
{
    public delegate void DocumentUpdatedEventHandler(IDocument document);


    public class Document : IDocument
    {
        
        public ObservableLinkedList<char> Content { get; } = new ObservableLinkedList<char>();

        public ObservableCollection<Line> Lines { get; } = new ObservableCollection<Line>();

        public List<TextLookBlock> TextLookBlocks { get; } = new List<TextLookBlock>();

        public string Text => new string(Content.ToArray());

        public int Length => Content.Count;

        private string name = "noname";

        public string Name { get => name; set { name = value; NameChanged?.Invoke(this); } }

        public string Path { get; set; } = string.Empty;



        public string LineEnding { get; } = "\r\n";

        public char[] WhiteDelimiters { get; } = new[] { '\r', '\n', ' ' };

        public char[] InvisibleCharacters { get; } = new[] { '\r', '\n' };

        public event DocumentUpdatedEventHandler Updated;
        public event DocumentUpdatedEventHandler FormatUpdated;
        public event DocumentUpdatedEventHandler NameChanged;

        public bool IsRevertingChanges => changes.IsRevertingChanges;


        private readonly ChangesBuffer changes;



        public Document(string text)
        {
            var textLines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);

            foreach (var textLine in textLines)
            {
                if (string.IsNullOrEmpty(textLine))
                {
                    Content.AddLast('\r');

                    Line line = new Line
                    {
                        Start = Content.Last,
                    };

                    Content.AddLast('\n');

                    line.End = Content.Last;

                    Lines.Add(line);
                }
                else
                {
                    Content.AddLast(textLine.First());

                    Line line = new Line
                    {
                        Start = Content.Last,
                    };

                    Content.AddLastRange(textLine.Substring(1) + "\r\n");

                    line.End = Content.Last;

                    Lines.Add(line);
                }
            }

            changes = new ChangesBuffer(this);
        }

        public (int inStringPosition, int row, int inRowPosition) GetPositionInText(Point point, double letterHeight, double letterWidth)
        {
            var left = point.X;
            var top = point.Y;

            int rowIndex = (int)(top / letterHeight);

            if (rowIndex >= Lines.Count)
            {
                if(Content.Last.Value == LineEnding[LineEnding.Length - 1])
                {
                    return GetPositionInText(Content.Last.GetAtOffset(-LineEnding.Length+1));
                }
                else
                {
                    return GetPositionInText(Content.Last);
                }
            }

            Line line = Lines[rowIndex];

            int inRowIndex = (int)(left / letterWidth);

            LinkedListNode<char> charElement;

            int inStringPosition;

            // If line length is less than inRowIndex then 
            // choose the last visible character.
            if (line.Text.Length - LineEnding.Length < inRowIndex)
            {
                charElement = line.End.Previous.Previous;
                inRowIndex = line.Text.Length - LineEnding.Length;
                inStringPosition = Content.IndexOf(charElement) + 1;
            }
            else
            {
                charElement = line.Start.GetAtOffset(inRowIndex);
                inStringPosition = Content.IndexOf(charElement);
            }

            return (inStringPosition, rowIndex, inRowIndex);
        }

        public (int inStringPosition, int row, int inRowPosition) GetPositionInText(int row, int inRowPosition)
        {
            var line = Lines[row];

            if (line.Length - 1 <= inRowPosition)
            {
                return GetPositionInText(line.End.Previous);
            }

            return GetPositionInText(line.Start.GetAtOffset(inRowPosition));
        }

        public (int inStringPosition, int row, int inRowPosition) GetPositionInText(int inStringPosition)
        {
            return GetPositionInText(Content.NodeAt(inStringPosition));
        }

        public (int inStringPosition, int row, int inRowPosition) GetPositionInText(LinkedListNode<char> node)
        {
            int inStringPosition = 0;
            int row = 0;

            foreach (var line in Lines)
            {
                var lineText = line.Text;

                if (lineText.Contains(node.Value))
                {
                    var lineNodes = line.Start.GetRangeNodes(lineText.Length).ToArray();

                    for (int inRowPosition = 0; inRowPosition < lineText.Length; inStringPosition++, inRowPosition++)
                    {
                        if (node == lineNodes[inRowPosition])
                        {
                            return (inStringPosition, row, inRowPosition);
                        }
                    }

                    // If not found then increment row index.
                    row++;
                }
                else
                {
                    inStringPosition += lineText.Length;
                    row++;
                }

            }

            return (-1, -1, -1);
        }
        
        public (LinkedListNode<char> start, LinkedListNode<char> end) GetWordOf(LinkedListNode<char> letter)
        {
            LinkedListNode<char> current = letter;

            LinkedListNode<char> left;

            // Find left end of word
            while (true)
            {
                if (IsWhiteDelimiter(current.Value))
                {
                    left = current.Next;
                    break;
                }
                else if(Content.First == current)
                {
                    left = Content.First;
                    break;
                }

                current = current.Previous;
            }

            LinkedListNode<char> right;

            // Reset current.
            current = letter;

            // Find left end of word
            while (true)
            {
                if (IsWhiteDelimiter(current.Value))
                {
                    right = current.Previous;
                    break;
                }
                else if (Content.Last == current)
                {
                    right = Content.Last;
                    break;
                }

                current = current.Next;
            }

            return (left, right);
        }

        public Point GetPositionInText(LinkedListNode<char> node, double letterHeight, double letterWidth)
        {
            var (inStringPosition, row, inRowPosition) = GetPositionInText(node);


            double x = inRowPosition * letterWidth;
            double y = row * letterHeight;

            return new Point(x, y);
        }


        public IEnumerable<LinkedListNode<char>> GetRange(int left, int right)
        {
            return Content.NodeAt(left).GetRangeNodes(right - left);
        }


        #region Edit

        public void Replace(IEnumerable<LinkedListNode<char>> nodes, char ch)
        {
            var posToInsert = Content.IndexOf(nodes.First());

            Delete(nodes);

            var posToInsertNode = Content.NodeAt(posToInsert);

            Insert(posToInsertNode, ch);
        }

        public void Replace(IEnumerable<LinkedListNode<char>> nodes, IEnumerable<char> collection)
        {
            var posToInsert = Content.IndexOf(nodes.First());

            Delete(nodes);

            var posToInsertNode = Content.NodeAt(posToInsert);

            Insert(posToInsertNode, collection);
        }


        public void InsertLineAfter(Line line, IEnumerable<char> collection)
        {
            BreakLine(line, line.End.Previous);

            var newLine = Lines[Lines.IndexOf(line) + 1];

            var strWithoutLineEnding = collection.Take(collection.Count() - LineEnding.Length);

            Insert(newLine.Start, strWithoutLineEnding);
        }


        public void Insert(LinkedListNode<char> position, IEnumerable<char> collection, bool shouldBreakLines = true)
        {
            var str = collection.ToStr();

            if (shouldBreakLines && str.Contains(LineEnding))
            {
                while (!string.IsNullOrEmpty(str))
                {
                    var index = str.IndexOf(LineEnding);

                    if (index == -1)
                    {
                        foreach (var item in collection)
                        {
                            Insert(position, item);
                        }

                        break;
                    }
                    else if (index == 0)
                    {
                        var pos = GetPositionInText(position);

                        var lineToBreak = Lines[pos.row];

                        BreakLine(lineToBreak, position);

                        str = str.Substring(LineEnding.Length);
                        collection = collection.Skip(LineEnding.Length);
                    }
                    else
                    {
                        foreach (var item in collection.Take(index))
                        {
                            Insert(position, item);
                        }

                        str = str.Substring(index);
                        collection = collection.Skip(index);
                    }
                }
            }
            else
            {
                foreach (var item in collection)
                {
                    Insert(position, item);
                }
            }
        }

        public void Insert(LinkedListNode<char> position, char ch)
        {
            Content.AddBefore(position, ch);

            changes.Add(new Insert(Content.IndexOf(position.Previous)));

            if (!LineEnding.Contains(ch) &&
                Lines.Any(n=>n.Start == position))
            {
                var line = Lines.First(n => n.Start == position);

                var index = Content.IndexOf(position);

                changes.Add(new MoveLineStart(line, Content.IndexOf(position)));

                line.Start = position.Previous;
            }

        }

        public void Insert(int inStringPosition, char ch)
        {
            Insert(Content.NodeAt(inStringPosition), ch);
        }



        public void Delete(int left, int right)
        {
            var nodesToDelete = GetRange(left, right);

            Delete(nodesToDelete);
        }

        public void Delete(int inStringPosition)
        {
            Delete(Content.NodeAt(inStringPosition));
        }

        public void Delete(IEnumerable<LinkedListNode<char>> nodes)
        {
            var str = nodes.Select(n => n.Value).ToStr();

            var secondLinesFromMerging = new List<Line>();

            if (str.Contains(LineEnding))
            {
                while (!string.IsNullOrEmpty(str))
                {
                    var index = str.IndexOf(LineEnding);

                    if (index == -1)
                    {
                        foreach (var node in nodes)
                        {
                            Delete(node, secondLinesFromMerging);
                        }

                        break;
                    }
                    else if (index == 0)
                    {
                        index = LineEnding.Length - 1;

                        var firstLine = Lines.First(n => n.End == nodes.ElementAt(index));

                        if (Lines.IndexOf(firstLine) + 1 == Lines.Count)
                        {
                            return;
                        }

                        var secondLine = Lines[Lines.IndexOf(firstLine) + 1];

                        MergeLines(firstLine, secondLine);

                        secondLinesFromMerging.Add(secondLine);

                        str = str.Skip(LineEnding.Length).ToStr();
                        nodes = nodes.Skip(LineEnding.Length);
                    }
                    else
                    {
                        foreach (var node in nodes.Take(index))
                        {
                            Delete(node, secondLinesFromMerging);
                        }

                        str = str.Skip(index).ToStr();
                        nodes = nodes.Skip(index);
                    }
                }
            }
            else
            {
                foreach (var node in nodes)
                {
                    Delete(node);
                }
            }
        }

        private void Delete(LinkedListNode<char> node, IEnumerable<Line> secondLinesFromMerging)
        {

            var lines = Lines.Concat(secondLinesFromMerging);
            var c = lines.Count(n => n.Start == node);
            if (lines.Any(n => n.Start == node))
            {
                var line = lines.First(n => n.Start == node);
                var index = Content.IndexOf(line.Start);

                changes.Add(new MoveLineStart(line, index));
                line.Start = node.Next;
            }
            else if (lines.Any(n => n.End == node))
            {
                //throw new Exception("is this place even reachable?");
                var line = lines.First(n => n.End == node);
                var index = Content.IndexOf(line.End);

                changes.Add(new MoveLineEnd(line, index));
                line.End = node.Previous;
            }
            changes.Add(new Delete(node.Value, Content.IndexOf(node)));

            Content.Remove(node);
        }

        public void Delete(LinkedListNode<char> node)
        {
            changes.Add(new Delete(node.Value, Content.IndexOf(node)));

            if (Lines.Any(n=>n.Start == node))
            {
                Lines.First(n => n.Start == node).Start = node.Next;
            }
            else if (Lines.Any(n => n.End == node))
            {
                Lines.First(n => n.End == node).End = node.Previous;
            }

            Content.Remove(node);
        }



        public void BreakLine(Line line, LinkedListNode<char> position)
        {
            Line newLine = new Line
            {
                Start = position,
                End = line.End,
            };

            Lines.Insert(Lines.IndexOf(line) + 1, newLine);

            Insert(position, "\r\n", false);

            // If line is empty then start is the first in newLine.
            if(line.Start == newLine.Start)
            {
                line.Start = position.GetAtOffset(-LineEnding.Length);
            }
            
            line.End = position.Previous;

            changes.Add(new LineBreak(line, newLine));
        }

        public void MergeLines(Line first, Line second)
        {

            var index = Content.IndexOf(second.End);
            changes.Add(new MoveLineEnd(second, index));

            //var indexStart = Content.IndexOf(second.Start);
            //changes.Add(new MoveLineStart(second, indexStart));

            changes.Add(new LineMerge(first, second));

            var firstEnd = first.End;
            var firstPreEnd = firstEnd.Previous;

            Delete(firstEnd);
            Delete(firstPreEnd);

            first.End = second.End;
            Lines.Remove(second);
        }




        public void StartChanges()
        {
            changes.Start();
        }

        public void CommitChanges()
        {
            changes.Commit();

            Updated?.Invoke(this);
        }

        public void RollbackChanges()
        {
            changes.RollBack();

            Updated?.Invoke(this);
        }

        #endregion

        #region Format

        public void ResetHighlight(bool clearSelection = false)
        {
            if (clearSelection)
            {
                TextLookBlocks.RemoveAll(n => n is HighlightBlock);
            }
            else
            {
                TextLookBlocks.RemoveAll(n => n is HighlightBlock && !n.Tags.Contains(Tag.Selection));
            }
        }

        public void ResetAllFormat(bool clearSelection = false)
        {
            if (clearSelection)
            {
                TextLookBlocks.Clear();
            }
            else
            {
                TextLookBlocks.RemoveAll(n => !n.Tags.Contains(Tag.Selection));
            }
            //TextLookBlocks.Clear();
        }

        public void ApplyHighlight((int start, int end)[] ranges, int[] tags, Brush brush, Pen pen = null)
        {
            foreach (var range in ranges)
            {
                // If selection is reversed then it means that 
                // selection is empty. Ignore this range.
                if (range.end < range.start)
                {
                    continue;
                }

                var t = new HighlightBlock
                {
                    Start = Content.NodeAt(range.start),
                    End = Content.NodeAt(range.end),
                    Brush = brush,
                    Pen = pen,
                    Tags = tags,
                };

                TextLookBlocks.Add(t);
            }

            FormatUpdated?.Invoke(this);
        }

        public void ApplyTextColor((int start, int end)[] ranges, int[] tags, Brush brush)
        {
            foreach (var range in ranges)
            {
                var t = new TextColorBlock
                {
                    Start = Content.NodeAt(range.start),
                    End = Content.NodeAt(range.end),
                    Brush = brush,
                    Tags = tags,
                };

                TextLookBlocks.Add(t);
            }

            FormatUpdated?.Invoke(this);
        }

        #endregion


        public (int start, int end)[] FindAll(string[] substrings, int startIndex, int endIndex)
        {
            var start = Content.NodeAt(startIndex);
            var end = Content.NodeAt(endIndex);

            var text = start.GetRange(end).ToStr();

            var indices = text.IndexOfAll(substrings);

            var unitednIndeices = indices
                .SelectMany((n, i) => n.Select(m => (m, m + substrings[i].Length - 1))).ToArray();

            return unitednIndeices;

        }


        public (int start, int end)[] FindAll(string substring, int startIndex, int endIndex)
        {
            var start = Content.NodeAt(startIndex);
            var end = Content.NodeAt(endIndex);

            var text = start.GetRange(end).ToStr();

            var indices = text.IndexOfAll(substring);

            return indices.Select(n => (n, n + substring.Length - 1)).ToArray();

        }

        private bool IsWhiteDelimiter(char ch)
        {
            return WhiteDelimiters.Contains(ch);
        }

    }


}