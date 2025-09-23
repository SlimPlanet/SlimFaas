using System.Reflection;
using GmailMailerApi.Models;
using GmailMailerApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Bind Smtp options
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton(sp =>
{
    var opt = new SmtpOptions();
    builder.Configuration.GetSection("Smtp").Bind(opt);
    return opt;
});
builder.Services.AddSingleton<EmailService>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    var xmlName = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlName);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);

    c.SwaggerDoc("v1", new()
    {
        Title = "Gmail Mailer API",
        Version = "v1",
        Description = "Send emails using Gmail SMTP (App Password). Two endpoints: JSON or multipart/form-data."
    });
});

var app = builder.Build();

// Swagger UI
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Gmail Mailer API v1");
    c.DocumentTitle = "Gmail Mailer API";
});

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("Health")
   .WithSummary("Liveness / readiness check.")
   .WithDescription("Returns a simple status payload to verify the service is running.")
   .Produces(StatusCodes.Status200OK);

// ------------------------------
// JSON route (no attachments)
// ------------------------------
app.MapPost("/mail/send-json",
    async Task<Results<Ok<string>, ValidationProblem>>(
        [FromServices] EmailService mailer,
        [FromBody] EmailRequestJson request,
        CancellationToken ct) =>
    {
        var problems = new Dictionary<string, string[]>();
        if (request.To is null || request.To.Count == 0)
            problems["To"] = ["At least one recipient is required."];
        if (string.IsNullOrWhiteSpace(request.Subject))
            problems["Subject"] = ["Subject is required."];
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Html))
            problems["Body"] = ["Provide 'Text' and/or 'Html'."];

        if (problems.Count > 0) return TypedResults.ValidationProblem(problems);

        await mailer.SendAsync(request, null, ct);
        return TypedResults.Ok("Email sent successfully.");
    })
   .WithName("SendEmailJson")
   .WithSummary("Send an email via Gmail SMTP using JSON.")
   .WithDescription("""
Accepts an application/json payload (no attachments).
Use this when you only need plain text and/or HTML bodies with recipients.
""")
   .Accepts<EmailRequestJson>("application/json")
   .Produces(StatusCodes.Status200OK)
   .ProducesValidationProblem()
   .DisableAntiforgery();

// -------------------------------------
// multipart/form-data route (attachments)
// -------------------------------------
app.MapPost("/mail/send-form",
    async Task<Results<Ok<string>, ValidationProblem>>(
        [FromServices] EmailService mailer,
        [FromForm] EmailRequestForm request,
        CancellationToken ct) =>
    {
        var problems = new Dictionary<string, string[]>();
        if (request.To is null || request.To.Count == 0)
            problems["To"] = ["At least one recipient is required."];
        if (string.IsNullOrWhiteSpace(request.Subject))
            problems["Subject"] = ["Subject is required."];
        if (string.IsNullOrWhiteSpace(request.Text) && string.IsNullOrWhiteSpace(request.Html))
            problems["Body"] = ["Provide 'Text' and/or 'Html'."];

        if (problems.Count > 0) return TypedResults.ValidationProblem(problems);

        await mailer.SendAsync(request, request.Attachments, ct);
        return TypedResults.Ok("Email sent successfully." );
    })
   .WithName("SendEmailForm")
   .WithSummary("Send an email via Gmail SMTP using multipart/form-data.")
   .WithDescription("""
Accepts multipart/form-data with optional file attachments.
Use this when you need to send files along with the email.
""")
   .Accepts<EmailRequestForm>("multipart/form-data")
   .Produces(StatusCodes.Status200OK)
   .ProducesValidationProblem()
   .DisableAntiforgery();

app.Run();
