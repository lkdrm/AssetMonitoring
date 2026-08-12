using AssetMonitoring.Modules.DeviceManagement.Domain.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetMonitoring.Modules.DeviceManagement.Application.Configurations;

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    //This comments I making for my self to have deeply understanding 
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        // Maps the Device entity to the Devices table
        // in the device_management SQL schema.
        builder.ToTable("Devices", "device_management");

        // Configures Id as the primary key.
        builder.HasKey(device => device.Id);

        // The Device domain constructor generates the Guid.
        // SQL Server must not generate or replace this value.
        builder.Property(device => device.Id).ValueGeneratedNever();

        // Code is required and can contain at most 50 characters.
        builder.Property(device => device.Code).HasMaxLength(50).IsRequired();

        // Creates a unique index that prevents duplicate device codes.
        builder.HasIndex(device => device.Code).IsUnique();

        builder.Property(device => device.Name).HasMaxLength(200).IsRequired();
        builder.Property(device => device.HardwareModel).HasMaxLength(100).IsRequired();
        builder.Property(device => device.HardwareRevision).HasMaxLength(50).IsRequired();
        builder.Property(device => device.FirmwareVersion).HasMaxLength(50).IsRequired();
        builder.Property(device => device.Location).HasMaxLength(200).IsRequired();

        // Stores the lifecycle enum as a readable string
        // instead of its numeric value.
        builder.Property(device => device.Lifecycle).HasConversion<string>().HasMaxLength(50).IsRequired();

        // Registration time is always required.
        builder.Property(device => device.RegisteredAtUtc).HasColumnType("datetime2")
            .HasConversion(value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
            .IsRequired();

        // Retirement time is optional.
        builder.Property(device => device.RetiredAtUtc).HasColumnType("datetime2")
            .HasConversion(value => value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null)
            .IsRequired(false);

        // Case-insensitive
        builder.Property(device => device.Code).HasMaxLength(50).UseCollation("Latin1_General_100_CI_AS").IsRequired();

        // The public property is a read-only view for application code.
        // EF Core must persist the private mutable collection instead.
        builder.Ignore(device => device.Capabilities);

        // Stores the private capability set as a JSON array
        // in one required SQL column.
        builder.PrimitiveCollection<List<DeviceCapability>>("_capabilities").HasColumnName("Capabilities").HasMaxLength(200).IsRequired();
    }
}
