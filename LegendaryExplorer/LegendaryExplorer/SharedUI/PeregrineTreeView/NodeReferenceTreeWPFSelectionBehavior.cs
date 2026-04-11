using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using LegendaryExplorer.Tools.ObjectReferenceViewer;
using Microsoft.Xaml.Behaviors;

// From https://stackoverflow.com/questions/183636/selecting-a-node-in-virtualized-treeview-with-wpf?answertab=votes#tab-top

namespace LegendaryExplorer.SharedUI.PeregrineTreeView
{
    public class NodeReferenceTreeWPFSelectionBehavior : Behavior<TreeView>
    {
        public ReferenceTreeWPF SelectedItem
        {
            get => (ReferenceTreeWPF)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(ReferenceTreeWPF), typeof(NodeReferenceTreeWPFSelectionBehavior),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedItemChanged));

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ReferenceTreeWPF oldNode)
            {
                oldNode.IsSelected = false;
            }

            if (e.NewValue is not ReferenceTreeWPF newNode) return;


            var behavior = (NodeReferenceTreeWPFSelectionBehavior)d;
            var tree = behavior.AssociatedObject;

            var nodeDynasty = new List<ReferenceTreeWPF> { newNode };
            var parent = newNode.Parent;
            while (parent != null)
            {
                nodeDynasty.Insert(0, parent);
                parent = parent.Parent;
            }

            var currentParent = (ItemsControl)tree;
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
            SelectedItem = e.NewValue as ReferenceTreeWPF;
        }

        #region Functions to get internal members using reflection

        // Some functionality we need is hidden in internal members, so we use reflection to get them

        #region ItemsControl.ItemsHost

        private static Panel GetItemsHost(ItemsControl itemsControl)
        {
            Debug.Assert(itemsControl != null);
            return ItemsHost(itemsControl);

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ItemsHost")]
            static extern Panel ItemsHost(ItemsControl itemsControlp);
        }

        #endregion ItemsControl.ItemsHost

        #region Panel.EnsureGenerator

        private static void CallEnsureGenerator(Panel panel)
        {
            Debug.Assert(panel != null);
            EnsureGenerator(panel);
            return;

            [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "EnsureGenerator")]
            static extern void EnsureGenerator(Panel panel);
        }

        #endregion Panel.EnsureGenerator

        #endregion Functions to get internal members using reflection
    }
}