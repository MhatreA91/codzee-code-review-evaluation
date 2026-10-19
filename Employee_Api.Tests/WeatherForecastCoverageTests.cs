using Employee_Api.Controllers;

namespace Employee_Api.Tests;

public class WeatherForecastCoverageTests
{
    [Fact]
    public void Forecast_Computed_TemperatureF()
    {
        // Arrange
        var forecast = new WeatherForecast
        {
            Date = new DateOnly(2026, 1, 1),
            TemperatureC = 20,
            Summary = "Mild"
        };

        // Act + Assert: pins every member of the template model.
        Assert.Equal(new DateOnly(2026, 1, 1), forecast.Date);
        Assert.Equal(20, forecast.TemperatureC);
        Assert.Equal(32 + (int)(20 / 0.5556), forecast.TemperatureF);
        Assert.Equal("Mild", forecast.Summary);
    }

    [Fact]
    public void Controller_Get_ReturnsFiveForecasts()
    {
        // Arrange
        var controller = new WeatherForecastController();

        // Act
        var result = controller.Get().ToList();

        // Assert
        Assert.Equal(5, result.Count);
        Assert.All(result, f => Assert.NotNull(f.Summary));
    }
}
