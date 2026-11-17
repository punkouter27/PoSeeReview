using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PoSeeReview.Client.Layout;

/// <summary>
/// Wires a FluentValidation <see cref="IValidator{T}"/> into Blazor's
/// <see cref="EditContext"/> so inline field errors render automatically in
/// EditForm / RadzenTemplateForm without reflection-heavy third-party packages.
/// The validators live in PoSeeReview.Shared, so the API endpoint runs the same rules and
/// answers a bypassed client with <c>Results.ValidationProblem</c>. (Fleet pattern, from PoTraffic.)
/// </summary>
public sealed class FluentValidationValidator<TModel> : ComponentBase, IDisposable where TModel : class
{
    private ValidationMessageStore? _messages;
    private EditContext? _subscribed;

    [CascadingParameter] private EditContext? CurrentEditContext { get; set; }

    [Parameter, EditorRequired] public IValidator<TModel> Validator { get; set; } = default!;

    protected override void OnInitialized()
    {
        if (CurrentEditContext is null)
            return;

        _subscribed = CurrentEditContext;
        _messages = new ValidationMessageStore(_subscribed);
        _subscribed.OnValidationRequested += OnValidationRequested;
        _subscribed.OnFieldChanged += OnFieldChanged;
    }

    private void OnValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        if (_subscribed is null || _messages is null || _subscribed.Model is not TModel model)
            return;

        _messages.Clear();
        ValidationResult result = Validator.Validate(model);
        foreach (ValidationFailure failure in result.Errors)
            _messages.Add(_subscribed.Field(failure.PropertyName), failure.ErrorMessage);

        _subscribed.NotifyValidationStateChanged();
    }

    private void OnFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (_subscribed is null || _messages is null || _subscribed.Model is not TModel model)
            return;

        _messages.Clear(e.FieldIdentifier);
        ValidationResult result = Validator.Validate(model, options => options.IncludeProperties(e.FieldIdentifier.FieldName));
        foreach (ValidationFailure failure in result.Errors)
            _messages.Add(e.FieldIdentifier, failure.ErrorMessage);

        _subscribed.NotifyValidationStateChanged();
    }

    public void Dispose()
    {
        if (_subscribed is null)
            return;

        _subscribed.OnValidationRequested -= OnValidationRequested;
        _subscribed.OnFieldChanged -= OnFieldChanged;
    }
}
