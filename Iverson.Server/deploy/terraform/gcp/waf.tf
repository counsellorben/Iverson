# CSR round-11 #5: the edge rate limit on Authentik's flow executor, the path every login and MFA
# submission uses. Counted per client address; requests over the limit in the window are blocked.
variable "authentik_login_rate_limit" {
  type    = number
  default = 100
}

variable "authentik_login_rate_window" {
  type    = number
  default = 300
  validation {
    condition     = contains([10, 30, 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700, 3600], var.authentik_login_rate_window)
    error_message = "authentik_login_rate_window must be one of 10, 30, 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700 or 3600 seconds, the intervals Cloud Armor rate limits accept."
  }
}

# Attached only to the Authentik Service's backend, through the GCPBackendPolicy the chart's GKE Gateway
# renders from authentik.ingress.securityPolicy. A client over the limit is banned for 10 minutes. The path
# is URL-decoded before it is matched, because Authentik decodes percent-encoded path segments.
resource "google_compute_security_policy" "authentik_login" {
  name = "${var.cluster_name}-authentik-login"

  rule {
    action   = "rate_based_ban"
    priority = 1000
    match {
      expr {
        expression = "request.path.urlDecode().startsWith('/api/v3/flows/executor/')"
      }
    }
    rate_limit_options {
      conform_action   = "allow"
      exceed_action    = "deny(429)"
      enforce_on_key   = "IP"
      ban_duration_sec = 600
      rate_limit_threshold {
        count        = var.authentik_login_rate_limit
        interval_sec = var.authentik_login_rate_window
      }
    }
  }

  rule {
    action   = "allow"
    priority = 2147483647
    match {
      versioned_expr = "SRC_IPS_V1"
      config {
        src_ip_ranges = ["*"]
      }
    }
  }
}

# Copy into values-gcp.yaml's authentik.ingress.securityPolicy (docs/runbooks/csr11-identity-plane.md).
output "authentik_security_policy_name" { value = google_compute_security_policy.authentik_login.name }
