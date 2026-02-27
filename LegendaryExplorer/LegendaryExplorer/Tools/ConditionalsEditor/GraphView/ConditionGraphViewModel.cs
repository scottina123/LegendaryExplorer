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
    /// Represents an arithmetic operator in an expression.
    /// </summary>
    public enum ArithmeticOperator
    {
        Add,
        Multiply
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
    /// ViewModel for a complex expression that cannot be represented as a simple comparison leaf.
    /// Displays the raw expression text as read-only within the graph structure.
    /// </summary>
    public class ConditionComplexLeafViewModel : ConditionNodeViewModel
    {
        private string _rawExpression;
        public string RawExpression
        {
            get => _rawExpression;
            set => SetProperty(ref _rawExpression, value);
        }

        public ICommand DeleteCommand { get; }

        public ConditionComplexLeafViewModel()
        {
            DeleteCommand = new GenericCommand(Delete);
        }

        public static ConditionComplexLeafViewModel Create(string rawExpression, ConditionGroupViewModel parent)
        {
            return new ConditionComplexLeafViewModel
            {
                _rawExpression = rawExpression,
                Parent = parent
            };
        }

        private void Delete()
        {
            Parent?.Children.Remove(this);
        }

        public override string Serialize() => RawExpression;
    }

    /// <summary>
    /// Base class for expression-level nodes (operands in comparisons and arithmetic).
    /// </summary>
    public abstract class ExpressionNodeViewModel : NotifyPropertyChangedBase
    {
        public abstract string Serialize();
    }

    /// <summary>
    /// A plot variable reference expression: plot.bools[x], plot.ints[x], plot.floats[x].
    /// </summary>
    public class PlotRefExpressionViewModel : ExpressionNodeViewModel
    {
        private PlotVarType _plotType;
        public PlotVarType PlotType
        {
            get => _plotType;
            set
            {
                if (SetProperty(ref _plotType, value))
                {
                    UpdatePlotPath();
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

        public static PlotRefExpressionViewModel Create(PlotVarType plotType, int plotIndex)
        {
            var vm = new PlotRefExpressionViewModel();
            vm._plotType = plotType;
            vm._plotIndex = plotIndex;
            vm.UpdatePlotPath();
            return vm;
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

        public override string Serialize() => PlotType switch
        {
            PlotVarType.Bool => $"plot.bools[{PlotIndex}]",
            PlotVarType.Int => $"plot.ints[{PlotIndex}]",
            PlotVarType.Float => $"plot.floats[{PlotIndex}]",
            _ => $"plot.bools[{PlotIndex}]"
        };
    }

    /// <summary>
    /// A literal value expression: i5, f3.5, a0, Bool true, etc.
    /// </summary>
    public class LiteralExpressionViewModel : ExpressionNodeViewModel
    {
        private string _value;
        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public static LiteralExpressionViewModel Create(string value)
        {
            return new LiteralExpressionViewModel { _value = value };
        }

        public override string Serialize() => Value;
    }

    /// <summary>
    /// An arithmetic expression: (a + b + c) or (a * b * c).
    /// </summary>
    public class ArithmeticExpressionViewModel : ExpressionNodeViewModel
    {
        private ArithmeticOperator _operator;
        public ArithmeticOperator Operator
        {
            get => _operator;
            set => SetProperty(ref _operator, value);
        }

        public ObservableCollection<ExpressionNodeViewModel> Operands { get; } = new();

        public static ArithmeticExpressionViewModel Create(ArithmeticOperator op, IEnumerable<ExpressionNodeViewModel> operands)
        {
            var vm = new ArithmeticExpressionViewModel();
            vm._operator = op;
            foreach (var operand in operands)
                vm.Operands.Add(operand);
            return vm;
        }

        public override string Serialize()
        {
            string opStr = Operator == ArithmeticOperator.Add ? " + " : " * ";
            return "(" + string.Join(opStr, Operands.Select(o => o.Serialize())) + ")";
        }
    }

    /// <summary>
    /// A comparison condition with arbitrary expression sides.
    /// Used for complex comparisons that can't be represented by <see cref="ConditionLeafViewModel"/>.
    /// </summary>
    public class ConditionComparisonViewModel : ConditionNodeViewModel
    {
        private ExpressionNodeViewModel _leftSide;
        public ExpressionNodeViewModel LeftSide
        {
            get => _leftSide;
            set => SetProperty(ref _leftSide, value);
        }

        private ComparisonOperator _operator;
        public ComparisonOperator Operator
        {
            get => _operator;
            set => SetProperty(ref _operator, value);
        }

        private ExpressionNodeViewModel _rightSide;
        public ExpressionNodeViewModel RightSide
        {
            get => _rightSide;
            set => SetProperty(ref _rightSide, value);
        }

        public ICommand DeleteCommand { get; }

        public ConditionComparisonViewModel()
        {
            DeleteCommand = new GenericCommand(Delete);
        }

        public static ConditionComparisonViewModel Create(
            ExpressionNodeViewModel left, ComparisonOperator op, ExpressionNodeViewModel right,
            ConditionGroupViewModel parent)
        {
            return new ConditionComparisonViewModel
            {
                _leftSide = left,
                _operator = op,
                _rightSide = right,
                Parent = parent
            };
        }

        private void Delete()
        {
            Parent?.Children.Remove(this);
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

        public override string Serialize()
        {
            return $"({LeftSide.Serialize()} {OperatorToString(Operator)} {RightSide.Serialize()})";
        }
    }

    /// <summary>
    /// A function call condition: Function :name Value:value.
    /// </summary>
    public class ConditionFunctionViewModel : ConditionNodeViewModel
    {
        private string _functionName = "";
        public string FunctionName
        {
            get => _functionName;
            set => SetProperty(ref _functionName, value);
        }

        private int _value;
        public int Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        private string _tag;
        public string Tag
        {
            get => _tag;
            set => SetProperty(ref _tag, value);
        }

        public ICommand DeleteCommand { get; }

        public ConditionFunctionViewModel()
        {
            DeleteCommand = new GenericCommand(Delete);
        }

        public static ConditionFunctionViewModel Create(string functionName, int value, ConditionGroupViewModel parent, string tag = null)
        {
            return new ConditionFunctionViewModel
            {
                _functionName = functionName,
                _value = value,
                _tag = tag,
                Parent = parent
            };
        }

        private void Delete()
        {
            Parent?.Children.Remove(this);
        }

        public override string Serialize()
        {
            string result = $"Function :{FunctionName} Value:{Value}";
            if (!string.IsNullOrEmpty(Tag))
                result += $" Tag:{Tag}";
            return result;
        }
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
        public ICommand AddExpressionCommand { get; }
        public ICommand AddFunctionCommand { get; }
        public ICommand AddGroupCommand { get; }
        public ICommand DeleteGroupCommand { get; }

        public ConditionGroupViewModel()
        {
            AddConditionCommand = new GenericCommand(AddCondition);
            AddExpressionCommand = new GenericCommand(AddExpression);
            AddFunctionCommand = new GenericCommand(AddFunction);
            AddGroupCommand = new GenericCommand(AddGroup);
            DeleteGroupCommand = new GenericCommand(DeleteGroup, CanDeleteGroup);
        }

        private void AddCondition()
        {
            var leaf = ConditionLeafViewModel.Create(PlotVarType.Bool, 0,
                ComparisonOperator.Equal, false, "0", this);
            Children.Add(leaf);
        }

        private void AddExpression()
        {
            var left = PlotRefExpressionViewModel.Create(PlotVarType.Int, 0);
            var right = LiteralExpressionViewModel.Create("i0");
            var comp = ConditionComparisonViewModel.Create(left, ComparisonOperator.Equal, right, this);
            Children.Add(comp);
        }

        private void AddFunction()
        {
            var func = ConditionFunctionViewModel.Create("", 0, this);
            Children.Add(func);
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
        public ICommand AddExpressionCommand { get; }
        public ICommand AddFunctionCommand { get; }
        public ICommand AddGroupCommand { get; }

        public ConditionGraphRootViewModel()
        {
            AddConditionCommand = new GenericCommand(AddConditionToRoot);
            AddExpressionCommand = new GenericCommand(AddExpressionToRoot);
            AddFunctionCommand = new GenericCommand(AddFunctionToRoot);
            AddGroupCommand = new GenericCommand(AddGroupToRoot);
        }

        private void AddConditionToRoot()
        {
            RootGroup?.AddConditionCommand.Execute(null);
        }

        private void AddExpressionToRoot()
        {
            RootGroup?.AddExpressionCommand.Execute(null);
        }

        private void AddFunctionToRoot()
        {
            RootGroup?.AddFunctionCommand.Execute(null);
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

            var node = ParseNode(text, null);
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

            return root;
        }

        private static ConditionNodeViewModel ParseNode(string text, ConditionGroupViewModel parent)
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
                        var child = ParseNode(part.Trim(), group);
                        child.Parent = group;
                        group.Children.Add(child);
                    }
                    return group;
                }

                // Try comparison: "left op right"
                var comparison = TryParseComparison(inner, parent);
                if (comparison != null) return comparison;

                // Try depth-aware comparison parsing for complex operands (e.g. arithmetic expressions)
                if (TrySplitAtComparisonOperator(inner, out string cmpLeft, out string cmpOp, out string cmpRight))
                {
                    var leftExpr = ParseExpression(cmpLeft);
                    var rightExpr = ParseExpression(cmpRight);
                    return ConditionComparisonViewModel.Create(leftExpr, ParseOperator(cmpOp), rightExpr, parent);
                }

                // Try parsing inner as a node (handles extra wrapping parens)
                var innerResult = ParseNode(inner, parent);
                if (innerResult is not ConditionComplexLeafViewModel)
                {
                    return innerResult;
                }

                // Inner was complex — preserve outer parens for correct serialization
                return ConditionComplexLeafViewModel.Create(text, parent);
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

            // Complex comparison (e.g. (arithmetic_expr) != 0)
            if (TrySplitAtComparisonOperator(text, out string bareLeft, out string bareOp, out string bareRight))
            {
                var leftExpr = ParseExpression(bareLeft);
                var rightExpr = ParseExpression(bareRight);
                return ConditionComparisonViewModel.Create(leftExpr, ParseOperator(bareOp), rightExpr, parent);
            }

            // Function call: "Function :name Value:value" optionally with " Tag:tag"
            var funcMatch = Regex.Match(text, @"^Function\s*:\s*(\S+)\s+Value\s*:\s*(-?\d+)(?:\s+Tag\s*:\s*(\S+))?$", RegexOptions.IgnoreCase);
            if (funcMatch.Success)
            {
                string funcName = funcMatch.Groups[1].Value;
                int funcValue = int.Parse(funcMatch.Groups[2].Value);
                string tag = funcMatch.Groups[3].Success ? funcMatch.Groups[3].Value : null;
                return ConditionFunctionViewModel.Create(funcName, funcValue, parent, tag);
            }

            // Unrecognized expression — editable text fallback
            return ConditionComplexLeafViewModel.Create(text, parent);
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

        /// <summary>
        /// Tries to find a comparison operator (==, !=, &lt;, &lt;=, &gt;, &gt;=) at depth 0 in the text
        /// and split into left operand, operator, and right operand.
        /// </summary>
        private static bool TrySplitAtComparisonOperator(string text, out string left, out string op, out string right)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && i + 1 < text.Length)
                {
                    string twoChar = text.Substring(i, 2);
                    if (twoChar is "==" or "!=" or "<=" or ">=")
                    {
                        left = text.Substring(0, i).Trim();
                        op = twoChar;
                        right = text.Substring(i + 2).Trim();
                        if (left.Length > 0 && right.Length > 0) return true;
                    }
                    if (c is '<' or '>' && text[i + 1] != '=')
                    {
                        left = text.Substring(0, i).Trim();
                        op = c.ToString();
                        right = text.Substring(i + 1).Trim();
                        if (left.Length > 0 && right.Length > 0) return true;
                    }
                }
            }
            left = op = right = null;
            return false;
        }

        /// <summary>
        /// Parses a value-level expression into an <see cref="ExpressionNodeViewModel"/> tree.
        /// Handles plot references, literals, and parenthesized arithmetic.
        /// </summary>
        private static ExpressionNodeViewModel ParseExpression(string text)
        {
            text = text.Trim();

            // Plot reference: plot.TYPE[index]
            var plotMatch = Regex.Match(text, @"^plot\.(bools|ints|floats)\[(-?\d+)\]$", RegexOptions.IgnoreCase);
            if (plotMatch.Success)
            {
                var type = ParsePlotVarType(plotMatch.Groups[1].Value);
                int index = int.Parse(plotMatch.Groups[2].Value);
                return PlotRefExpressionViewModel.Create(type, index);
            }

            // Parenthesized expression (arithmetic or extra wrapping parens)
            if (text.StartsWith("(") && FindMatchingCloseParen(text, 0) == text.Length - 1)
            {
                string inner = text.Substring(1, text.Length - 2).Trim();

                // Try splitting by + at depth 0
                var addParts = SplitByTopLevelArithmetic(inner, '+');
                if (addParts is { Count: > 1 })
                {
                    return ArithmeticExpressionViewModel.Create(
                        ArithmeticOperator.Add, addParts.Select(p => ParseExpression(p.Trim())));
                }

                // Try splitting by * at depth 0
                var mulParts = SplitByTopLevelArithmetic(inner, '*');
                if (mulParts is { Count: > 1 })
                {
                    return ArithmeticExpressionViewModel.Create(
                        ArithmeticOperator.Multiply, mulParts.Select(p => ParseExpression(p.Trim())));
                }

                // Extra wrapping parens
                return ParseExpression(inner);
            }

            // Literal (catch-all): i5, f3.5, a0, Bool true, false, etc.
            return LiteralExpressionViewModel.Create(text);
        }

        /// <summary>
        /// Splits an expression by a single-character arithmetic operator (+, *) at depth 0.
        /// Returns null if the operator was not found.
        /// </summary>
        private static List<string> SplitByTopLevelArithmetic(string text, char op)
        {
            var parts = new List<string>();
            int depth = 0;
            int start = 0;
            bool found = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && c == op)
                {
                    parts.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                    found = true;
                }
            }

            if (found)
            {
                parts.Add(text.Substring(start).Trim());
                return parts;
            }
            return null;
        }
    }
}
