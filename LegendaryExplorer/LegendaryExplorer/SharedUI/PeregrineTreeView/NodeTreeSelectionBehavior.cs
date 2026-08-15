using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Xaml.Behaviors;

// From https://stackoverflow.com/questions/183636/selecting-a-node-in-virtualized-treeview-with-wpf?answertab=votes#tab-top

namespace LegendaryExplorer.SharedUI.PeregrineTreeView
{
    public class NodeTreeSelectionBehavior : Behavior<TreeView>
    {
        private static readonly TimeSpan DeferredSelectionDelay = TimeSpan.FromMilliseconds(100);

        private int _selectionVersion;
        private bool _isUnselectingPreviousItem;

        public TreeViewEntry SelectedItem
        {
            get => (TreeViewEntry)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register("SelectedItem", typeof(TreeViewEntry), typeof(NodeTreeSelectionBehavior),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

        public bool DeferContainerRealization
        {
            get => (bool)GetValue(DeferContainerRealizationProperty);
            set => SetValue(DeferContainerRealizationProperty, value);
        }

        public static readonly DependencyProperty DeferContainerRealizationProperty =
            DependencyProperty.Register(nameof(DeferContainerRealization), typeof(bool), typeof(NodeTreeSelectionBehavior));

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var behavior = (NodeTreeSelectionBehavior)d;
            int selectionVersion = ++behavior._selectionVersion;

            if (e.OldValue is TreeViewEntry oldNode)
            {
                behavior._isUnselectingPreviousItem = true;
                try
                {
                    oldNode.IsSelected = false;
                }
                finally
                {
                    behavior._isUnselectingPreviousItem = false;
                }
            }

            var newNode = e.NewValue as TreeViewEntry;
            if (newNode == null) return;

            bool delayBeforeSelection = !newNode.IsProgramaticallySelecting;
            newNode.IsProgramaticallySelecting = false;

            var tree = behavior.AssociatedObject;
            if (ReferenceEquals(tree.SelectedItem, newNode))
            {
                return;
            }

            if (behavior.DeferContainerRealization)
            {
                _ = behavior.SelectItemDeferredAsync(newNode, selectionVersion, delayBeforeSelection);
                return;
            }

            var nodeDynasty = new List<TreeViewEntry> { newNode };
            var parent = newNode.Parent;
            while (parent != null)
            {
                nodeDynasty.Insert(0, parent);
                parent = parent.Parent;
            }

            var currentParent = tree as ItemsControl;
            foreach (var node in nodeDynasty)
            {
                // first try the easy way
                if (!TryGetTreeViewItem(currentParent, node, out TreeViewItem newParent))
                {
                    return;
                }

                if (newParent == null)
                {
                    return;
                    //throw new InvalidOperationException("Tree view item cannot be found or created for node '" + node + "'");
                }

                if (node == newNode)
                {
                    newParent.IsSelected = true;
                    newParent.BringIntoView();
                    break;
                }

                newParent.IsExpanded = true;
                currentParent = newParent;
            }
        }

        private async Task SelectItemDeferredAsync(TreeViewEntry newNode, int selectionVersion, bool delayBeforeSelection)
        {
            // Debounce ordinary binding changes, but let explicit navigation begin
            // immediately. The selection version still cancels stale realizations.
            if (delayBeforeSelection)
            {
                await Task.Delay(DeferredSelectionDelay);
            }

            if (selectionVersion != _selectionVersion || _isCleanedUp)
            {
                return;
            }

            var nodeDynasty = new List<TreeViewEntry> { newNode };
            for (TreeViewEntry parent = newNode.Parent; parent is not null; parent = parent.Parent)
            {
                nodeDynasty.Insert(0, parent);
            }

            ItemsControl currentParent = AssociatedObject;
            foreach (TreeViewEntry node in nodeDynasty)
            {
                TreeViewItem newParent = await TryGetTreeViewItemDeferredAsync(currentParent, node, selectionVersion);
                if (newParent is null || selectionVersion != _selectionVersion || _isCleanedUp)
                {
                    return;
                }

                if (ReferenceEquals(node, newNode))
                {
                    newParent.IsSelected = true;
                    newParent.BringIntoView();

                    // BringIndexIntoView can initially realize only the requested
                    // row, leaving a blank viewport. Let WPF process the scroll,
                    // then measure the surrounding virtualized rows once.
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    if (selectionVersion != _selectionVersion
                        || _isCleanedUp
                        || !ReferenceEquals(newParent.DataContext, newNode))
                    {
                        return;
                    }

                    AssociatedObject.UpdateLayout();
                    newParent.BringIntoView();
                    return;
                }

                bool needsExpansion = !newParent.IsExpanded;
                newParent.IsExpanded = true;
                currentParent = newParent;
                if (needsExpansion)
                {
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Loaded);
                    if (selectionVersion != _selectionVersion || _isCleanedUp)
                    {
                        return;
                    }
                }
            }
        }

        private async Task<TreeViewItem> TryGetTreeViewItemDeferredAsync(
            ItemsControl currentParent,
            object node,
            int selectionVersion)
        {
            if (currentParent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem existingContainer)
            {
                return existingContainer;
            }

            currentParent.ApplyTemplate();
            if (currentParent.Template.FindName("ItemsHost", currentParent) is ItemsPresenter itemsPresenter)
            {
                itemsPresenter.ApplyTemplate();
            }

            int index = currentParent.Items.IndexOf(node);
            if (index < 0)
            {
                Debug.WriteLine($"Skipping tree selection for node '{node}' because it is no longer present in the current container.");
                return null;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (selectionVersion != _selectionVersion || _isCleanedUp)
                {
                    return null;
                }

                if (GetItemsHost(currentParent) is VirtualizingPanel virtualizingPanel)
                {
                    CallEnsureGenerator(virtualizingPanel);
                    try
                    {
                        virtualizingPanel.BringIndexIntoViewPublic(index);
                    }
                    catch
                    {
                    }
                }

                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Loaded);
                if (currentParent.ItemContainerGenerator.ContainerFromIndex(index) is TreeViewItem generatedContainer)
                {
                    return generatedContainer;
                }
            }

            return null;
        }

        private static bool TryGetTreeViewItem(ItemsControl currentParent, object node, out TreeViewItem newParent)
        {
            newParent = currentParent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (newParent != null)
            {
                return true;
            }

            currentParent.ApplyTemplate();
            var itemsPresenter = (ItemsPresenter)currentParent.Template.FindName("ItemsHost", currentParent);
            if (itemsPresenter != null)
            {
                itemsPresenter.ApplyTemplate();
            }
            else
            {
                currentParent.UpdateLayout();
            }

            var virtualizingPanel = GetItemsHost(currentParent) as VirtualizingPanel;
            if (virtualizingPanel != null)
            {
                CallEnsureGenerator(virtualizingPanel);
            }

            int index = currentParent.Items.IndexOf(node);
            if (index < 0)
            {
                Debug.WriteLine($"Skipping tree selection for node '{node}' because it is no longer present in the current container.");
                return false;
            }

            if (virtualizingPanel != null)
            {
                try
                {
                    virtualizingPanel.BringIndexIntoViewPublic(index);
                }
                catch
                {
                    //This seems to be an internal exception
                }
            }

            newParent = currentParent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            if (newParent != null)
            {
                return true;
            }

            currentParent.UpdateLayout();
            if (virtualizingPanel != null)
            {
                try
                {
                    virtualizingPanel.BringIndexIntoViewPublic(index);
                }
                catch
                {
                    //This seems to be an internal exception
                    return false;
                }
            }

            newParent = currentParent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            return newParent != null;
        }

        private bool _isCleanedUp;

        private void Cleanup()
        {
            if (!_isCleanedUp)
            {
                _isCleanedUp = true;
                _selectionVersion++;
                AssociatedObject.SelectedItemChanged -= OnTreeViewSelectedItemChanged;
                AssociatedObject.Unloaded -= AssociatedObjectOnUnloaded;
            }
        }
        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject.Unloaded += AssociatedObjectOnUnloaded;
            AssociatedObject.SelectedItemChanged += OnTreeViewSelectedItemChanged;
        }

        private void AssociatedObjectOnUnloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        protected override void OnDetaching()
        {
            Cleanup();
            base.OnDetaching();
        }

        private void OnTreeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Clearing the old node above makes TreeView briefly report a null
            // selection. Do not feed that transient value back into the behavior:
            // it would advance _selectionVersion and cancel the deferred selection
            // of the replacement node before its virtualized container is realized.
            if (_isUnselectingPreviousItem && e.NewValue is null)
            {
                return;
            }

            SelectedItem = e.NewValue as TreeViewEntry;
        }

        #region Functions to get internal members using reflection

        // Some functionality we need is hidden in internal members, so we use reflection to get them

        #region ItemsControl.ItemsHost

        static readonly PropertyInfo ItemsHostPropertyInfo = typeof(ItemsControl).GetProperty("ItemsHost", BindingFlags.Instance | BindingFlags.NonPublic);

        private static Panel GetItemsHost(ItemsControl itemsControl)
        {
            Debug.Assert(itemsControl != null);
            return ItemsHostPropertyInfo.GetValue(itemsControl, null) as Panel;
        }

        #endregion ItemsControl.ItemsHost

        #region Panel.EnsureGenerator

        private static readonly MethodInfo EnsureGeneratorMethodInfo = typeof(Panel).GetMethod("EnsureGenerator", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void CallEnsureGenerator(Panel panel)
        {
            Debug.Assert(panel != null);
            EnsureGeneratorMethodInfo.Invoke(panel, null);
        }

        #endregion Panel.EnsureGenerator

        #region VirtualizingPanel.BringIndexIntoView

        private static readonly MethodInfo BringIndexIntoViewMethodInfo = typeof(VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void CallBringIndexIntoView(VirtualizingPanel virtualizingPanel, int index)
        {
            Debug.Assert(virtualizingPanel != null);
            BringIndexIntoViewMethodInfo.Invoke(virtualizingPanel, new object[] { index });
        }

        #endregion VirtualizingPanel.BringIndexIntoView

        #endregion Functions to get internal members using reflection
    }
}
