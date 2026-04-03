using AeroStream.Ingestion;
using Xunit;

namespace AeroStream.Tests;

public class TelemetryTests
{
    [Fact]
    public void Altitude_ShouldNotBeNegative_UsingCSharp14Field()
    {
        // Arrange
        double negativeAltitude = -50.0;

        // Act
        var record = new TelemetryRecord(
            Guid.NewGuid(), 
            39.36, -9.14, 45.0, 1.0, 0.0, 180.0, 
            DateTime.UtcNow
        ) 
        { 
            Altitude = negativeAltitude 
        };

        // Assert
        // The 'field' logic in our record should have converted -50.0 to 0
        Assert.Equal(0, record.Altitude);
    }
}