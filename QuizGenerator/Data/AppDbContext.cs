using Microsoft.EntityFrameworkCore;
using QuizGenerator.Models;

namespace QuizGenerator.Data;

/// <summary>
/// Bu DbContext KENDİ tablolarını yönetmiyor - senin ana backend'inin (movie-quiz-main)
/// ZATEN VAR OLAN "Questions", "Content", "Categories" tablolarına bağlanıyor.
///
/// ÖNEMLİ: Bu yüzden Program.cs içinde db.Database.Migrate() ÇAĞRILMIYOR ve bu
/// DbContext için migration oluşturulMAMALI. Tablolar zaten ana backend'in migration'ları
/// (ya da elle) tarafından oluşturulmuş durumda - bu servis sadece onlara okuma/yazma yapan
/// bir "sidecar". İki farklı DbContext'in aynı tablolara migration uygulamaya çalışması
/// (ayrı __EFMigrationsHistory kayıtlarıyla) veri bütünlüğünü bozabilir.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<ContentEntity> Contents => Set<ContentEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuestionEntity>().ToTable("Questions");
        modelBuilder.Entity<ContentEntity>().ToTable("Contents"); // yeni DB'de çoğul - eskisinde tekildi
        modelBuilder.Entity<CategoryEntity>().ToTable("Categories");
    }
}
