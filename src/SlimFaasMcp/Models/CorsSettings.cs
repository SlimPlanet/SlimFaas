namespace SlimFaasMcp.Models;

public class CorsSettings
{
    public string[]? Origins { get; set; }      // ex: ["*"] ou ["https://*.axa.com","http://localhost:*"]
    public string[]? Methods { get; set; }      // ex: ["*"] ou ["GET","POST","OPTIONS"]
    public string[]? Headers { get; set; }      // ex: ["*"] ou ["Authorization","Content-Type"]
    public string[]? Expose  { get; set; }      // ex: ["WWW-Authenticate"]
    public bool Credentials { get; set; } = false;
    public int? MaxAgeMinutes { get; set; } = 60;
}
