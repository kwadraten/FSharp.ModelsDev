namespace FSharp.ModelsDev.Providers

open System
open System.Net.Http

module internal ProviderAuthAdapters =
    let create (options: ProviderAuthOptions) (http: HttpClient) (clock: TimeProvider): IProviderAuthAdapter =
        match options.ProviderId with
        | "grok" -> GrokAuthAdapter(options, http, clock)
        | "openrouter" -> OpenRouterAuthAdapter(options, http, clock)
        | "kimi-code-plan-cn" | "kimi-code-plan-global" -> KimiCodeAuthAdapter(options, http, clock)
        | "github-copilot" -> GitHubCopilotAuthAdapter(options, http, clock)
        | "kilo" -> KiloAuthAdapter(options, http, clock)
        | "cline-pass" -> ClinePassAuthAdapter(options, http, clock)
        | _ -> raise (NotSupportedException("Unsupported subscription authentication provider: " + options.ProviderId))
