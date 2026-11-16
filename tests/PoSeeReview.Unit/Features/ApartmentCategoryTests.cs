using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Shared.Enums;
using Xunit;

namespace PoSeeReview.Unit.Features;

/// <summary>
/// Apartments: found by their own Google types, scored by the worse of strange and awful, and
/// sent to the model with a rubric that keeps the prompt cache and asks for the horror rating.
/// </summary>
public sealed class ApartmentCategoryTests
{
    [Fact]
    public void ApartmentRubric_StaysAboveTheCacheThreshold_AndAsksForHorror()
    {
        Assert.True(ChatPrompts.ApartmentAnalysisSystemMessage.Length >= 4400,
            $"Apartment system message is {ChatPrompts.ApartmentAnalysisSystemMessage.Length} chars; keep it above the 1024-token cache threshold.");
        Assert.Contains("reviewIds", ChatPrompts.ApartmentAnalysisSystemMessage);
        Assert.Contains("\"horror\"", ChatPrompts.ApartmentAnalysisSystemMessage);
        Assert.Contains("\"horror\"", ChatPrompts.JsonSchemaFor(PlaceCategory.Apartment).ToString());
        Assert.DoesNotContain("horror", ChatPrompts.JsonSchemaFor(PlaceCategory.Restaurant).ToString());
        Assert.Same(ChatPrompts.AnalysisSystemMessage, ChatPrompts.SystemMessageFor(PlaceCategory.Restaurant));
    }

    [Theory]
    [InlineData(PlaceCategory.Apartment, 4, 8, 70, 8)]   // a sewage saga: horror carries it
    [InlineData(PlaceCategory.Apartment, 9, 2, 79, 2)]   // a surreal building: absurdity carries it
    [InlineData(PlaceCategory.Restaurant, 4, 8, 35, 0)]  // restaurants never read horror
    public void ToAnalysis_ScoresTheWorseOfStrangeAndAwful_ForApartmentsOnly(
        PlaceCategory category, int absurdity, int horror, int expectedScore, int expectedHorror)
    {
        var analysis = ChatPrompts.ToAnalysis(new StrangenessAnalysisResult
        {
            Absurdity = absurdity,
            Horror = horror,
            Specificity = 7,
            StoryPotential = 7,
            Narrative = "Sewage came up through the bathtub.",
            Panels = [new PanelResult { Scene = "A tenant stares at a bubbling tub.", Caption = "The tub gurgles.", ReviewIds = [1] }]
        }, reviewCount: 3, category);

        Assert.Equal(expectedScore, analysis.StrangenessScore);
        Assert.Equal(expectedHorror, analysis.Ratings!.Horror);
    }

    [Theory]
    [InlineData("apartment_building,point_of_interest", PlaceCategory.Apartment)]
    [InlineData("condominium_complex", PlaceCategory.Apartment)]
    [InlineData("apartment_complex,restaurant", PlaceCategory.Restaurant)] // found as a restaurant, scored as one
    [InlineData("restaurant,food", PlaceCategory.Restaurant)]
    [InlineData("", PlaceCategory.Restaurant)]
    public void CategoryFromTypes_IsApartmentOnlyForHousingThatIsNotAlsoARestaurant(string types, PlaceCategory expected)
    {
        var list = types.Length == 0 ? null : types.Split(',');
        Assert.Equal(expected, GoogleMapsService.CategoryFromTypes(list));
    }

    [Theory]
    [InlineData(null, true, PlaceCategory.Restaurant)]
    [InlineData("Apartments", true, PlaceCategory.Apartment)]
    [InlineData("restaurant", true, PlaceCategory.Restaurant)]
    [InlineData("hotel", false, PlaceCategory.Restaurant)]
    public void Category_ParsesTheWireNames_AndRejectsAnythingElse(string? value, bool ok, PlaceCategory expected)
    {
        Assert.Equal(ok, PlaceCategories.TryParse(value, out var category));
        Assert.Equal(expected, category);
        Assert.Equal(expected, PlaceCategories.FromWire(expected.ToWire()));
    }
}
