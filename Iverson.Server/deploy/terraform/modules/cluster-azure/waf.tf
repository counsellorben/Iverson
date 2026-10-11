# CSR round-11 #5: the Application Gateway AGIC programs, owned here so it can be WAF_v2. It sits in
# 10.1.16.0/24, the range the add-on's own gateway used, so global.trustedProxies.cidrs on the Azure
# profile still names the gateway, and in this resource group, which the add-on identity is granted
# Network Contributor on below.
resource "azurerm_subnet" "appgw" {
  name                 = "${var.cluster_name}-appgw-subnet"
  resource_group_name  = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = ["10.1.16.0/24"]
}

resource "azurerm_public_ip" "appgw" {
  name                = "${var.cluster_name}-appgw-pip"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  allocation_method   = "Static"
  sku                 = "Standard"
}

# The gateway-wide policy, for every host but Authentik's: OWASP in Detection, which logs and never
# blocks.
resource "azurerm_web_application_firewall_policy" "gateway" {
  name                = "${var.cluster_name}-waf-gateway"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  policy_settings {
    mode = "Detection"
  }

  managed_rules {
    managed_rule_set {
      type    = "OWASP"
      version = "3.2"
    }
  }
}

# The Authentik Ingress's per-path policy (authentik.ingress.wafPolicyId). Prevention, because a
# rate rule blocks only in Prevention; the managed rule set the policy requires therefore also blocks
# on the Authentik host. The path is URL-decoded before it is matched, because Authentik decodes
# percent-encoded path segments.
resource "azurerm_web_application_firewall_policy" "authentik" {
  name                = "${var.cluster_name}-waf-authentik"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name

  policy_settings {
    mode = "Prevention"
  }

  custom_rules {
    name                 = "authentiklogin"
    priority             = 1
    rule_type            = "RateLimitRule"
    action               = "Block"
    rate_limit_duration  = var.authentik_login_rate_window == 60 ? "OneMin" : "FiveMins"
    rate_limit_threshold = var.authentik_login_rate_limit
    group_rate_limit_by  = "ClientAddr"

    match_conditions {
      match_variables {
        variable_name = "RequestHeaders"
        selector      = "Host"
      }
      operator     = "Equal"
      match_values = [var.authentik_host]
      transforms   = ["Lowercase"]
    }

    match_conditions {
      match_variables {
        variable_name = "RequestUri"
      }
      operator     = "BeginsWith"
      match_values = ["/api/v3/flows/executor/"]
      transforms   = ["UrlDecode"]
    }
  }

  managed_rules {
    managed_rule_set {
      type    = "OWASP"
      version = "3.2"
    }
  }
}

# Terraform owns only the shell AGIC needs to exist; AGIC writes the listeners, pools, rules and
# probes from the Ingresses, so those are ignored here.
resource "azurerm_application_gateway" "agic" {
  name                = "${var.cluster_name}-appgw"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  firewall_policy_id  = azurerm_web_application_firewall_policy.gateway.id

  sku {
    name     = "WAF_v2"
    tier     = "WAF_v2"
    capacity = 1
  }

  gateway_ip_configuration {
    name      = "gw"
    subnet_id = azurerm_subnet.appgw.id
  }

  frontend_port {
    name = "http"
    port = 80
  }

  frontend_ip_configuration {
    name                 = "public"
    public_ip_address_id = azurerm_public_ip.appgw.id
  }

  backend_address_pool {
    name = "default"
  }

  backend_http_settings {
    name                  = "default"
    cookie_based_affinity = "Disabled"
    port                  = 80
    protocol              = "Http"
  }

  http_listener {
    name                           = "default"
    frontend_ip_configuration_name = "public"
    frontend_port_name             = "http"
    protocol                       = "Http"
  }

  request_routing_rule {
    name                       = "default"
    rule_type                  = "Basic"
    http_listener_name         = "default"
    backend_address_pool_name  = "default"
    backend_http_settings_name = "default"
    priority                   = 1
  }

  lifecycle {
    ignore_changes = [
      backend_address_pool, backend_http_settings, frontend_port, http_listener, probe,
      request_routing_rule, url_path_map, ssl_certificate, redirect_configuration, tags,
    ]
  }
}

# azure-cli grants this when it enables the add-on with a gateway id; Terraform's gateway_id does
# not, and without it AGIC cannot program the gateway (VA 44).
resource "azurerm_role_assignment" "agic_network_contributor" {
  scope                = azurerm_resource_group.this.id
  role_definition_name = "Network Contributor"
  principal_id         = azurerm_kubernetes_cluster.this.ingress_application_gateway[0].ingress_application_gateway_identity[0].object_id
}
