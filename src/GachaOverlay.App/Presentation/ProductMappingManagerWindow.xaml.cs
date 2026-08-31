namespace GachaOverlay.App.Presentation;

public partial class ProductMappingManagerWindow : System.Windows.Window
{
    private ProductMappingManagerViewModel? _viewModel;

    public ProductMappingManagerWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = args.NewValue as ProductMappingManagerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProductMappingManagerViewModel.IsDraftMapping) &&
            _viewModel?.IsDraftMapping == true)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ProductNameTextBox.Focus();
                ProductNameTextBox.SelectAll();
            });
        }
    }
}
