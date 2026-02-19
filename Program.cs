using WorkProgress.Services;

DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

var dbConn = Environment.GetEnvironmentVariable("DB_CONNECTION");
if (!string.IsNullOrEmpty(dbConn))
    builder.Configuration["ConnectionStrings:DefaultConnection"] = dbConn;

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<DbService>();
builder.Services.AddSingleton<ColorAnalysisService>();
builder.Services.AddSingleton<TemplateMaskService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=WorkProgress}/{action=Index}/{id?}");

app.Run();
