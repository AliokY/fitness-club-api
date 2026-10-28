using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DataAccessLayer.Configurations;

public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Operation).HasColumnName("operation");
        builder.Property(x => x.KeyHash).HasColumnName("key_hash").HasMaxLength(64);
        builder.Property(x => x.Key).HasColumnName("key");
        builder.Property(x => x.ClassId).HasColumnName("class_id");
        builder.Property(x => x.Seats).HasColumnName("seats");
        builder.Property(x => x.ResponseJson).HasColumnName("response_json");
        builder.HasIndex(x => new { x.UserId, x.Operation, x.KeyHash }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
    }
}
