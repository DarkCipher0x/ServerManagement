# Dcx.Cloud

The provider-neutral cloud layer (ADR-020: catalog, hardened HTTP with retries, JSON helpers, the provider contract, its forms and documents) and the providers on it, one folder each: **Vultr/**, **Hetzner/**, **Contabo/**, **Azure/** and **Gcp/** (Google Cloud), each an auth/client, a catalog and a provider; `Cloud/CloudJwt.cs` signs the two clouds' key-based sign-ins.

May reference: Dcx.Core, Dcx.Connectivity, Dcx.Ui (WPF only for the providers' own forms and menu items; the resource screens are in the app).
