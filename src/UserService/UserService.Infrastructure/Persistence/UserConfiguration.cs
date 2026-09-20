using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

/// <summary>
/// Column names are written out in snake_case rather than taken from EF's PascalCase
/// default. PostgreSQL folds unquoted identifiers to lower case, so a PascalCase column
/// only works when every identifier is quoted — which means every hand-written query,
/// every psql session, and every index filter expression has to quote it too, forever.
/// A naming-convention package would do this automatically; one extra dependency to
/// avoid typing them once was not worth it for three tables.
/// </summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        // Guid v7 is time-ordered, so it indexes like a sequential key while staying
        // globally unique. A random v4 fragments the index and turns every insert into
        // a page split somewhere in the middle of the B-tree.
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();

        // THE join key between Entra and this database. Unique, because two local rows
        // for one Entra principal means the user's addresses appear and disappear
        // depending on which row a lookup happens to find — and it is the JIT
        // provisioning race that creates them (see CurrentUserProvider).
        builder.Property(u => u.EntraObjectId).HasColumnName("entra_object_id").IsRequired();
        builder.HasIndex(u => u.EntraObjectId).IsUnique().HasDatabaseName("ux_users_entra_object_id");

        builder.Property(u => u.EntraTenantId).HasColumnName("entra_tenant_id").IsRequired();

        // citext so 'Ada@example.com' and 'ada@example.com' are one value to the
        // database. NOT unique: Entra owns email uniqueness, and duplicating that
        // constraint here would reject a user whose address Entra already accepted.
        builder.Property(u => u.Email).HasColumnName("email").HasColumnType("citext");
        builder.HasIndex(u => u.Email).HasDatabaseName("ix_users_email");

        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(256);
        builder.Property(u => u.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(u => u.AnonymizedAt).HasColumnName("anonymized_at");

        // Addresses are part of the User aggregate: they load and save with it, and are
        // never queried independently of their owner.
        builder.HasMany(u => u.Addresses)
            .WithOne()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(u => u.Addresses)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude();
    }
}

internal sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("addresses");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(a => a.Label).HasColumnName("label").HasMaxLength(64);
        builder.Property(a => a.Line1).HasColumnName("line1").IsRequired().HasMaxLength(256);
        builder.Property(a => a.Line2).HasColumnName("line2").HasMaxLength(256);
        builder.Property(a => a.City).HasColumnName("city").IsRequired().HasMaxLength(128);
        builder.Property(a => a.PostalCode).HasColumnName("postal_code").IsRequired().HasMaxLength(32);
        builder.Property(a => a.Country).HasColumnName("country").IsRequired().HasMaxLength(2).IsFixedLength();
        builder.Property(a => a.IsDefaultShipping).HasColumnName("is_default_shipping").IsRequired();
        builder.Property(a => a.IsDefaultBilling).HasColumnName("is_default_billing").IsRequired();

        builder.HasIndex(a => a.UserId).HasDatabaseName("ix_addresses_user_id");

        // Belt and braces for the "at most one default" invariant the aggregate already
        // enforces. The aggregate is the primary guard because it fails at the point of
        // the mistake with a readable message; these partial unique indexes catch the
        // case the aggregate cannot see — two concurrent transactions each setting a
        // different default, each perfectly valid in isolation.
        builder.HasIndex(a => a.UserId)
            .IsUnique()
            .HasFilter("is_default_shipping")
            .HasDatabaseName("ux_addresses_one_default_shipping");

        builder.HasIndex(a => a.UserId)
            .IsUnique()
            .HasFilter("is_default_billing")
            .HasDatabaseName("ux_addresses_one_default_billing");
    }
}
