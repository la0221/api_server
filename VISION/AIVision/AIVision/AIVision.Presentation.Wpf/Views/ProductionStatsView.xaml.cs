using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AIVision.Application.Contracts.ProductionStats;
using AIVision.Presentation.Wpf.ViewModels;

namespace AIVision.Presentation.Wpf.Views;

public partial class ProductionStatsView : Window
{
    private readonly ProductionStatsViewModel _viewModel;

    public ProductionStatsView(ProductionStatsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_viewModel.Orders.Count == 0 && _viewModel.QueryCommand.CanExecute(null))
        {
            _viewModel.QueryCommand.Execute(null);
        }
    }

    private void OnOrdersSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            _viewModel.UpdateSelection(grid.SelectedItems.OfType<WorkOrderSummaryDto>());
        }
    }
}
