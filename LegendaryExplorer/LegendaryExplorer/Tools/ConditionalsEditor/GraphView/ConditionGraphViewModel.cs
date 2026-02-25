using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI;
using LegendaryExplorerCore.Misc;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.PlotDatabase;
using LegendaryExplorerCore.PlotDatabase.PlotElements;
using LegendaryExplorerCore.Unreal;

namespace LegendaryExplorer.Tools.ConditionalsEditor.GraphView
{
    /// <summary>
    /// Represents a plot variable type in a condition.
    /// </summary>
    public enum PlotVarType
    {
        Bool,
        Int,
        Float
    }

    /// <summary>
    /// Represents a comparison operator.
    /// </summary>
    public enum ComparisonOperator
    {
        Equal,
        NotEqual,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual
    }

    /// <summary>
    /// Represents a logical operator for groups.
    /// </summary>
    public enum LogicalOperator
    {
        And,
        Or
    }

    /// <summary>
    /// Base class for items in the condition graph tree.
    /// </summary>
    public abstract class ConditionNodeViewModel : NotifyPropertyChangedBase
    {
        private ConditionGroupViewModel _parent;
        public ConditionGroupViewModel Parent
        {
            get => _parent;
            set => SetProperty(ref _parent, value);
        }

        /// <summary>
        /// Serializes this node back to the text format understood by <see cref="ME3ConditionalsCompiler"/>.
        /// </summary>
        public abstract string Serialize();
    }

    /// <summary>
    /// ViewModel for a single leaf condition (e.g. plot.bools[123], plot.ints[456] == i5).
    /// </summary>
    public class ConditionLeafViewModel : ConditionNodeViewModel
    {
        private PlotVarType _plotType;
        public PlotVarType PlotType
        {
            get => _plotType;
            set
            {
                if (SetProperty(ref _plotType, value))
                {
                    OnPropertyChanged(nameof(IsBoolType));
                    OnPropertyChanged(nameof(IsNumericType));
                    UpdatePlotPath();
                    // Reset value defaults on type change
                    if (value == PlotVarType.Bool)
                    {
                        BoolValue = true;
                        Operator = ComparisonOperator.Equal;
                    }
                    else
                    {
                        NumericValue = "0";
                    }
                }
            }
        }

        private int _plotIndex;
        public int PlotIndex
        {
            get => _plotIndex;
            set
            {
                if (SetProperty(ref _plotIndex, value))
                {
                    UpdatePlotPath();
                }
            }
        }

        private string _plotPath;
        public string PlotPath
        {
            get => _plotPath;
            set => SetProperty(ref _plotPath, value);
        }

        private ComparisonOperator _operator = ComparisonOperator.Equal;
        public ComparisonOperator Operator
        {
            get => _operator;
            set => SetProperty(ref _operator, value);
        }

        private bool _boolValue = true;
        public bool BoolValue
        {
            get => _boolValue;
            set => SetProperty(ref _boolValue, value);
        }

        private string _numericValue = "0";
        public string NumericValue
        {
            get => _numericValue;
            set => SetProperty(ref _numericValue, value);
        }

        public bool IsBoolType => PlotType == PlotVarType.Bool;
        public bool IsNumericType => PlotType != PlotVarType.Bool;

        public ICommand DeleteCommand { get; }

        public ConditionLeafViewModel()
        {
            DeleteCommand = new GenericCommand(Delete);
        }

        /// <summary>
        /// Factory method that sets all fields directly, bypassing <see cref="SetProperty{T}"/>
        /// to avoid default-value equality checks dropping the assignment.
        /// Call this instead of using object initializers from the parser.
        /// </summary>
        public static ConditionLeafViewModel Create(PlotVarType plotType, int plotIndex,
            ComparisonOperator op, bool boolValue, string numericValue,
            ConditionGroupViewModel parent)
        {
            var vm = new ConditionLeafViewModel();
            vm._plotType = plotType;
            vm._plotIndex = plotIndex;
            vm._operator = op;
            vm._boolValue = boolValue;
            vm._numericValue = numericValue;
            vm.Parent = parent;
            vm.UpdatePlotPath();
            return vm;
        }

        private void Delete()
        {
            Parent?.Children.Remove(this);
        }

        private void UpdatePlotPath()
        {
            PlotElement element = PlotType switch
            {
                PlotVarType.Bool => PlotDatabases.FindPlotBoolByID(_plotIndex, MEGame.LE3),
                PlotVarType.Int => PlotDatabases.FindPlotIntByID(_plotIndex, MEGame.LE3),
                PlotVarType.Float => PlotDatabases.FindPlotFloatByID(_plotIndex, MEGame.LE3),
                _ => null
            };
            PlotPath = element?.Path;
        }

        public override string Serialize()
        {
            string plotRef = PlotType switch
            {
                PlotVarType.Bool => $"plot.bools[{PlotIndex}]",
                PlotVarType.Int => $"plot.ints[{PlotIndex}]",
                PlotVarType.Float => $"plot.floats[{PlotIndex}]",
                _ => $"plot.bools[{PlotIndex}]"
            };

            if (PlotType == PlotVarType.Bool)
            {
                // Always use expression form to preserve original bytecode structure.
                // Bare "plot.bools[x]" compiles to a table lookup (0x60), while
                // "(plot.bools[x] == Bool true)" compiles to an expression (0x50).
                // Using expression form ensures round-trip fidelity.
                string boolStr = BoolValue ? "true" : "false";
                return $"({plotRef} {OperatorToString(Operator)} Bool {boolStr})";
            }
            else
            {
                string valPrefix = PlotType == PlotVarType.Float ? "f" : "i";
                return $"({plotRef} {OperatorToString(Operator)} {valPrefix}{NumericValue})";
            }
        }

        private static string OperatorToString(ComparisonOperator op) => op switch
        {
            ComparisonOperator.Equal => "==",
            ComparisonOperator.NotEqual => "!=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessOrEqual => "<=",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterOrEqual => ">=",
            _ => "=="
        };
    }

    /// <summary>
    /// ViewModel for a logical group (AND/OR) containing child conditions or sub-groups.
    /// </summary>
    public class ConditionGroupViewModel : ConditionNodeViewModel
    {
        private LogicalOperator _logicalOperator = LogicalOperator.And;
        public LogicalOperator LogicalOperator
        {
            get => _logicalOperator;
            set => SetProperty(ref _logicalOperator, value);
        }

        public ObservableCollection<ConditionNodeViewModel> Children { get; } = new();

        public ICommand AddConditionCommand { get; }
        public ICommand AddGroupCommand { get; }
        public ICommand DeleteGroupCommand { get; }

        public ConditionGroupViewModel()
        {
            AddConditionCommand = new GenericCommand(AddCondition);
            AddGroupCommand = new GenericCommand(AddGroup);
            DeleteGroupCommand = new GenericCommand(DeleteGroup, CanDeleteGroup);
        }

        private void AddCondition()
        {
            var leaf = ConditionLeafViewModel.Create(PlotVarType.Bool, 0,
                ComparisonOperator.Equal, false, "0", this);
            Children.Add(leaf);
        }

        private void AddGroup()
        {
            var group = new ConditionGroupViewModel
            {
                LogicalOperator = LogicalOperator == LogicalOperator.And ? LogicalOperator.Or : LogicalOperator.And,
                Parent = this
            };
            Children.Add(group);
        }

        private void DeleteGroup()
        {
            Parent?.Children.Remove(this);
        }

        private bool CanDeleteGroup() => Parent != null;

        public override string Serialize()
        {
            if (Children.Count == 0)
                return "Bool false";

            if (Children.Count == 1)
            {
                return Children[0].Serialize();
            }

            string opStr = LogicalOperator == LogicalOperator.And ? " && " : " || ";
            var parts = Children.Select(c => c.Serialize());
            return "(" + string.Join(opStr, parts) + ")";
        }

        /// <summary>
        /// Returns true if <paramref name="candidate"/> is this group or any descendant group.
        /// Used to prevent circular drag-drop.
        /// </summary>
        public bool IsOrContains(ConditionNodeViewModel candidate)
        {
            if (candidate == this) return true;
            foreach (var child in Children)
            {
                if (child == candidate) return true;
                if (child is ConditionGroupViewModel subGroup && subGroup.IsOrContains(candidate))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Root ViewModel for the condition graph view. Wraps the top-level group.
    /// </summary>
    public class ConditionGraphRootViewModel : NotifyPropertyChangedBase
    {
        private ConditionGroupViewModel _rootGroup;
        public ConditionGroupViewModel RootGroup
        {
            get => _rootGroup;
            set => SetProperty(ref _rootGroup, value);
        }

        /// <summary>
        /// When true, the decompiled text was fully parsed into the graph and
        /// can be safely serialized back. When false, the expression is too complex
        /// for the graph editor and should be displayed as read-only text.
        /// </summary>
        private bool _isFullyParsed = true;
        public bool IsFullyParsed
        {
            get => _isFullyParsed;
            set
            {
                if (SetProperty(ref _isFullyParsed, value))
                {
                    OnPropertyChanged(nameof(IsReadOnly));
                }
            }
        }

        /// <summary>
        /// Inverse of <see cref="IsFullyParsed"/> for XAML binding convenience.
        /// </summary>
        public bool IsReadOnly => !IsFullyParsed;

        /// <summary>
        /// The original decompiled text, stored for display when the graph
        /// cannot represent the expression.
        /// </summary>
        private string _rawText;
        public string RawText
        {
            get => _rawText;
            set => SetProperty(ref _rawText, value);
        }

        public ICommand AddConditionCommand { get; }
        public ICommand AddGroupCommand { get; }

        public ConditionGraphRootViewModel()
        {
            AddConditionCommand = new GenericCommand(AddConditionToRoot);
            AddGroupCommand = new GenericCommand(AddGroupToRoot);
        }

        private void AddConditionToRoot()
        {
            RootGroup?.AddConditionCommand.Execute(null);
        }

        private void AddGroupToRoot()
        {
            RootGroup?.AddGroupCommand.Execute(null);
        }

        /// <summary>
        /// Serializes the entire graph back to conditional text for the compiler.
        /// </summary>
        public string Serialize()
        {
            return RootGroup?.Serialize() ?? "Bool false";
        }

        public bool TryValidate(out string message)
        {
            message = null;
            if (RootGroup == null)
            {
                message = "Graph has no root group.";
                return false;
            }

            foreach (var group in EnumerateGroups(RootGroup))
            {
                if (group.Parent != null && group.Children.Count < 2)
                {
                    message = "All nested groups must contain at least two conditions. Add another condition or delete the group.";
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<ConditionGroupViewModel> EnumerateGroups(ConditionGroupViewModel group)
        {
            yield return group;
            foreach (var child in group.Children)
            {
                if (child is ConditionGroupViewModel childGroup)
                {
                    foreach (var nested in EnumerateGroups(childGroup))
                    {
                        yield return nested;
                    }
                }
            }
        }

        /// <summary>
        /// Parses decompiled conditional text into the graph ViewModel tree.
        /// </summary>
        public static ConditionGraphRootViewModel FromDecompiledText(string text)
        {
            var root = new ConditionGraphRootViewModel();
            root.RawText = text;
            text = text.Trim();

            if (string.IsNullOrEmpty(text))
            {
                root.RootGroup = new ConditionGroupViewModel { LogicalOperator = LogicalOperator.And };
                return root;
            }

            bool hadFallback = false;
            var node = ParseNode(text, null, ref hadFallback);
            if (node is ConditionGroupViewModel group)
            {
                root.RootGroup = group;
            }
            else
            {
                // Single leaf — wrap in a group
                var wrapper = new ConditionGroupViewModel { LogicalOperator = LogicalOperator.And };
                node.Parent = wrapper;
                wrapper.Children.Add(node);
                root.RootGroup = wrapper;
            }

            root.IsFullyParsed = !hadFallback;
            return root;
        }

        private static ConditionNodeViewModel ParseNode(string text, ConditionGroupViewModel parent, ref bool hadFallback)
        {
            text = text.Trim();

            // Bool literal: "Bool true" / "Bool false"
            if (text.Equals("Bool true", StringComparison.OrdinalIgnoreCase))
            {
                return ConditionLeafViewModel.Create(PlotVarType.Bool, 0,
                    ComparisonOperator.Equal, true, "0", parent);
            }
            if (text.Equals("Bool false", StringComparison.OrdinalIgnoreCase))
            {
                return ConditionLeafViewModel.Create(PlotVarType.Bool, 0,
                    ComparisonOperator.Equal, false, "0", parent);
            }

            // Bare plot.bools[x] — treated as bool == true
            var bareBoolMatch = Regex.Match(text, @"^plot\.bools\[(-?\d+)\]$", RegexOptions.IgnoreCase);
            if (bareBoolMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Bool,
                    int.Parse(bareBoolMatch.Groups[1].Value),
                    ComparisonOperator.Equal, true, "0", parent);
            }

            // Parenthesized expression
            if (text.StartsWith("(") && FindMatchingCloseParen(text, 0) == text.Length - 1)
            {
                string inner = text.Substring(1, text.Length - 2).Trim();

                // Try splitting by top-level && or ||
                var (parts, logicalOp) = SplitByTopLevelLogical(inner);
                if (parts != null && parts.Count > 1)
                {
                    var group = new ConditionGroupViewModel
                    {
                        LogicalOperator = logicalOp,
                        Parent = parent
                    };
                    foreach (var part in parts)
                    {
                        var child = ParseNode(part.Trim(), group, ref hadFallback);
                        child.Parent = group;
                        group.Children.Add(child);
                    }
                    return group;
                }

                // Try comparison: "left op right"
                var comparison = TryParseComparison(inner, parent);
                if (comparison != null) return comparison;

                // Fallback: try parsing inner as a node (handles extra wrapping parens)
                return ParseNode(inner, parent, ref hadFallback);
            }

            // Bare plot.ints[x] != 0 (produced by decompiler for int-as-bool)
            var intNonZeroMatch = Regex.Match(text, @"^plot\.ints\[(-?\d+)\]\s*!=\s*0$", RegexOptions.IgnoreCase);
            if (intNonZeroMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Int,
                    int.Parse(intNonZeroMatch.Groups[1].Value),
                    ComparisonOperator.NotEqual, false, "0", parent);
            }

            // Bare plot.floats[x] != 0
            var floatNonZeroMatch = Regex.Match(text, @"^plot\.floats\[(-?\d+)\]\s*!=\s*0$", RegexOptions.IgnoreCase);
            if (floatNonZeroMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Float,
                    int.Parse(floatNonZeroMatch.Groups[1].Value),
                    ComparisonOperator.NotEqual, false, "0", parent);
            }

            // Fallback: expression too complex for graph editor
            hadFallback = true;
            return ConditionLeafViewModel.Create(PlotVarType.Bool, 0,
                ComparisonOperator.Equal, false, "0", parent);
        }

        private static ConditionNodeViewModel TryParseComparison(string inner, ConditionGroupViewModel parent)
        {
            // Bool comparison: "plot.bools[x] OP Bool true" or "plot.bools[x] OP Bool false"
            // The decompiler produces "Bool true" / "Bool false" as the RHS for bool comparisons
            var boolCompMatch = Regex.Match(inner,
                @"^plot\.bools\[(-?\d+)\]\s*(==|!=|<=|>=|<|>)\s*Bool\s+(true|false)$", RegexOptions.IgnoreCase);
            if (boolCompMatch.Success)
            {
                bool val = boolCompMatch.Groups[3].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                var op = ParseOperator(boolCompMatch.Groups[2].Value);
                return ConditionLeafViewModel.Create(PlotVarType.Bool,
                    int.Parse(boolCompMatch.Groups[1].Value),
                    op, val, "0", parent);
            }

            // Bool comparison: "plot.bools[x] == false" (legacy/alternate format)
            var boolFalseMatch = Regex.Match(inner, @"^plot\.bools\[(-?\d+)\]\s*==\s*false$", RegexOptions.IgnoreCase);
            if (boolFalseMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Bool,
                    int.Parse(boolFalseMatch.Groups[1].Value),
                    ComparisonOperator.Equal, false, "0", parent);
            }

            // Int comparison: "plot.ints[x] OP iVALUE" or "plot.ints[x] OP VALUE"
            var intMatch = Regex.Match(inner, @"^plot\.ints\[(-?\d+)\]\s*(==|!=|<=|>=|<|>)\s*i?(-?\d+)$", RegexOptions.IgnoreCase);
            if (intMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Int,
                    int.Parse(intMatch.Groups[1].Value),
                    ParseOperator(intMatch.Groups[2].Value), false,
                    intMatch.Groups[3].Value, parent);
            }

            // Float comparison: "plot.floats[x] OP fVALUE"
            var floatMatch = Regex.Match(inner, @"^plot\.floats\[(-?\d+)\]\s*(==|!=|<=|>=|<|>)\s*f?(-?[\d.]+)$", RegexOptions.IgnoreCase);
            if (floatMatch.Success)
            {
                return ConditionLeafViewModel.Create(PlotVarType.Float,
                    int.Parse(floatMatch.Groups[1].Value),
                    ParseOperator(floatMatch.Groups[2].Value), false,
                    floatMatch.Groups[3].Value, parent);
            }

            // Generic comparison: "plot.TYPE[x] OP plot.TYPE[y]" — try to represent the left side
            var genericCompMatch = Regex.Match(inner,
                @"^plot\.(bools|ints|floats)\[(-?\d+)\]\s*(==|!=|<=|>=|<|>)\s*plot\.(bools|ints|floats)\[(-?\d+)\]$",
                RegexOptions.IgnoreCase);
            if (genericCompMatch.Success)
            {
                var leftType = ParsePlotVarType(genericCompMatch.Groups[1].Value);
                return ConditionLeafViewModel.Create(leftType,
                    int.Parse(genericCompMatch.Groups[2].Value),
                    ParseOperator(genericCompMatch.Groups[3].Value), false,
                    genericCompMatch.Groups[5].Value, parent);
            }

            return null;
        }

        private static PlotVarType ParsePlotVarType(string s) => s.ToLowerInvariant() switch
        {
            "bools" => PlotVarType.Bool,
            "ints" => PlotVarType.Int,
            "floats" => PlotVarType.Float,
            _ => PlotVarType.Bool
        };

        private static ComparisonOperator ParseOperator(string op) => op switch
        {
            "==" => ComparisonOperator.Equal,
            "!=" => ComparisonOperator.NotEqual,
            "<" => ComparisonOperator.LessThan,
            "<=" => ComparisonOperator.LessOrEqual,
            ">" => ComparisonOperator.GreaterThan,
            ">=" => ComparisonOperator.GreaterOrEqual,
            _ => ComparisonOperator.Equal
        };

        /// <summary>
        /// Splits an expression string by top-level && or || (not inside parentheses).
        /// </summary>
        private static (List<string> parts, LogicalOperator op) SplitByTopLevelLogical(string text)
        {
            var parts = new List<string>();
            LogicalOperator? detectedOp = null;
            int depth = 0;
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0)
                {
                    if (i + 1 < text.Length)
                    {
                        string twoChar = text.Substring(i, 2);
                        if (twoChar == "&&")
                        {
                            if (detectedOp == null) detectedOp = LogicalOperator.And;
                            else if (detectedOp != LogicalOperator.And) continue; // mixed — skip

                            parts.Add(text.Substring(start, i - start).Trim());
                            i += 1; // skip second &
                            start = i + 1;
                        }
                        else if (twoChar == "||")
                        {
                            if (detectedOp == null) detectedOp = LogicalOperator.Or;
                            else if (detectedOp != LogicalOperator.Or) continue;

                            parts.Add(text.Substring(start, i - start).Trim());
                            i += 1;
                            start = i + 1;
                        }
                    }
                }
            }

            if (detectedOp != null)
            {
                parts.Add(text.Substring(start).Trim());
                return (parts, detectedOp.Value);
            }

            return (null, LogicalOperator.And);
        }

        /// <summary>
        /// Finds the index of the closing ')' that matches the opening '(' at <paramref name="openIndex"/>.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindMatchingCloseParen(string text, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

            }
        }
