// The Go conformance driver.
//
// Mirrors the Python driver's shape (Iverson.Clients/Python/conformance/driver.py): reports,
// never asserts. Every step's failure is data — ok:false with an error message — and the process
// still exits 0. A non-zero exit means the driver itself broke (bad flags, unsupported scenario,
// unwritable --out).
//
// Invoked as `bin/conformance <flags>` with cwd Iverson.Clients/Go, after
// `go build -o bin/conformance ./conformance` (DriverRunner.cs:105-107).
package main

import (
	"context"
	"crypto/md5"
	"encoding/json"
	"fmt"
	"net"
	"net/url"
	"os"
	"strings"

	"google.golang.org/grpc"
	"google.golang.org/protobuf/encoding/protojson"

	"github.com/iverson/clients/go/iverson"

	pb "github.com/iverson/clients/go/generated"
)

const (
	language = "go"
)

// supportedScenarios lists every scenario this driver implements. naming-rejected (S2) is
// register-phase-only: the orchestrator never invokes this driver for any other phase under it.
var supportedScenarios = map[string]bool{
	"crud-roundtrip":  true,
	"naming-rejected": true,
}

// ── Argument parsing ──────────────────────────────────────────────────────────

// args is a minimal `--flag value` parser, mirroring the Python driver's Args.
type args struct {
	values map[string]string
}

func parseArgs(argv []string) args {
	values := make(map[string]string)
	i := 0
	for i < len(argv) {
		flag := argv[i]
		if !strings.HasPrefix(flag, "--") {
			i++
			continue
		}
		// The next argument is the value whatever it looks like: the harness always emits
		// `--flag <value>` pairs (empty string included), and legitimate values — a base64
		// token, a JSON blob — can begin with "--". Treating a leading "--" as "no value"
		// would silently drop them.
		if i+1 < len(argv) {
			values[flag] = argv[i+1]
			i += 2
		} else {
			values[flag] = ""
			i++
		}
	}
	return args{values: values}
}

func (a args) require(flag string) (string, error) {
	v, ok := a.values[flag]
	if !ok || v == "" {
		return "", fmt.Errorf("missing required flag %s", flag)
	}
	return v, nil
}

func (a args) optional(flag string) string {
	return a.values[flag]
}

// grpcDialTarget reduces a gRPC endpoint to the bare `host:port` target grpc.Dial accepts,
// tolerating both the scheme-qualified form the harness sends and an already-bare one. Returns an
// error rather than a best guess so the driver fails as a driver (non-zero exit) instead of
// reporting an unresolvable target as a client-library conformance failure.
func grpcDialTarget(addr string) (string, error) {
	withScheme := addr
	if !strings.Contains(addr, "://") {
		withScheme = "http://" + addr
	}
	u, err := url.Parse(withScheme)
	if err != nil || u.Host == "" {
		return "", fmt.Errorf("unusable --grpc value %q", addr)
	}
	if u.Port() == "" {
		if u.Scheme == "https" {
			return net.JoinHostPort(u.Hostname(), "443"), nil
		}
		return net.JoinHostPort(u.Hostname(), "80"), nil
	}
	return u.Host, nil
}

// ── Step result / phase document ─────────────────────────────────────────────

// stepResult is one step's outcome within a phase document. All fields are always present (null
// where absent) via explicit json tags, matching the .NET driver's JsonSerializerDefaults.Web
// output, which does not omit nulls.
type stepResult struct {
	Name           string            `json:"name"`
	Ok             bool              `json:"ok"`
	Error          *string           `json:"error"`
	TypeDescriptor json.RawMessage   `json:"typeDescriptor"`
	Keys           map[string]string `json:"keys"`
	Entity         json.RawMessage   `json:"entity"`
}

type phaseDocument struct {
	Language string       `json:"language"`
	Phase    string       `json:"phase"`
	Steps    []stepResult `json:"steps"`
}

func failStep(name string, err error) stepResult {
	msg := err.Error()
	return stepResult{Name: name, Ok: false, Error: &msg}
}

func okStep(name string) stepResult {
	return stepResult{Name: name, Ok: true}
}

func entityJSON(entity interface{}) json.RawMessage {
	b, err := json.Marshal(entity)
	if err != nil {
		return nil
	}
	return json.RawMessage(b)
}

// deriveKey returns a deterministic per-run key: distinct across runs because --id-prefix is.
// Only needs to be consistent within this driver's own fallback path — cross-language key
// equality is not required, since --keys is language-qualified (each language reads only its own
// slice).
func deriveKey(idPrefix, logicalName string) string {
	sum := md5.Sum([]byte(idPrefix + ":" + logicalName))
	return formatUUID(sum)
}

func formatUUID(b [16]byte) string {
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

func parseKeys(keysJSON, lang string) map[string]string {
	out := map[string]string{}
	if keysJSON == "" {
		return out
	}
	var byLanguage map[string]map[string]string
	if err := json.Unmarshal([]byte(keysJSON), &byLanguage); err != nil {
		return out
	}
	if m, ok := byLanguage[lang]; ok {
		return m
	}
	return out
}

// ── Read reporting ────────────────────────────────────────────────────────────

// reportGet runs one Get and reports it in the same shape the other four drivers report.
//
// Go's EntityCoordinator.Get is the only one of the five that turns a not-found response into an
// error (iverson/coordinator.go returns "entity not found" when !resp.Found); .NET, Python,
// TypeScript and Java all hand back a null entity on an otherwise successful call. That is a
// client-API difference, not a conformance signal, and left alone it would make get_after_delete
// — the step whose whole purpose is separating gone from denied from broken — structurally
// different in Go. So the shape is flattened here, in the driver, never in the client.
//
// Nothing is judged: this does not decide that not-found is correct, only that "the server said
// Found=false" is reported the way the other four report it (ok:true, entity null). A denial or a
// transport failure still reports ok:false with the client's own error text. The two are told
// apart by asking the server itself through the public retrieval stub rather than by matching on
// an error string.
func reportGet(
	ctx context.Context,
	client *iverson.IversonClient,
	name, typeName, key string,
	do func() (interface{}, error),
) stepResult {
	entity, err := do()
	if err == nil {
		step := okStep(name)
		step.Entity = entityJSON(entity)
		return step
	}

	resp, probeErr := client.RetrievalStub.Get(ctx, &pb.RetrievalRequest{TypeName: typeName, Key: key})
	if probeErr == nil && !resp.Found {
		return okStep(name)
	}

	return failStep(name, err)
}

// staticServiceToken attaches an already-minted service token to every call, the identity the
// server reads out of the `authorization` header, plus the acting-user identity the server reads
// out of `x-acting-user-authorization`.
//
// Both are required, and they authorize different things: the service token carries the
// schema_admin scope RegisterSchema needs, while every row read and write is authorized against
// the acting user. Emitting only the service half lets registration succeed and then denies every
// write with `actor=unknown` — a PermissionDenied that names nothing about identity, surfacing
// phases away from its cause. This mirrors the Java driver's DualHeaderCredentials.
//
// It carries the acting token as a field rather than reading it back out of the context, because
// the context key iverson.WithActingUserToken writes under is unexported; replacing the client's
// own OAuth2ClientCredentials (which does read it) means taking over both headers here.
type staticServiceToken struct {
	token       string
	actingToken string
}

func (s staticServiceToken) GetRequestMetadata(_ context.Context, _ ...string) (map[string]string, error) {
	md := map[string]string{"authorization": "Bearer " + s.token}
	if s.actingToken != "" {
		md[iverson.ActingUserMetadataKey] = "Bearer " + s.actingToken
	}
	return md, nil
}

func (s staticServiceToken) RequireTransportSecurity() bool { return false }

// typeNameOf reports the type name the client itself derives for an entity, so the raw retrieval
// probe above addresses exactly the type EntityCoordinator addresses.
func typeNameOf(entity interface{}) (string, error) {
	meta, err := iverson.InspectType(entity)
	if err != nil {
		return "", err
	}
	return meta.TypeName, nil
}

// ── Descriptor capture ────────────────────────────────────────────────────────

type capturedType struct {
	name string
	json string
}

// capturingMappingClient wraps the real ObjectMappingService stub — the sanctioned capture seam
// per the plan: NewSchemaRegistrar(client MappingClient, ...) takes the client as a public
// constructor parameter, and MappingClient is a one-method interface (iverson/registrar.go:12-16)
// that the generated stub's variadic-opts method does not itself satisfy. Records the outgoing
// SchemaRequest.RootType of every registration call (before forwarding, so it is captured even if
// the RPC itself fails) and forwards unchanged. Nothing is judged here — the JSON is reported
// verbatim.
type capturingMappingClient struct {
	real     pb.ObjectMappingServiceClient
	captured []capturedType
}

func (c *capturingMappingClient) RegisterSchema(ctx context.Context, req *pb.SchemaRequest) (*pb.SchemaResponse, error) {
	if req.RootType != nil {
		js, err := protojson.Marshal(req.RootType)
		if err == nil {
			c.captured = append(c.captured, capturedType{name: req.RootType.TypeName, json: string(js)})
		}
	}
	return c.real.RegisterSchema(ctx, req)
}

// selectDescriptor returns the descriptor for the first of preferredTypeNames actually sent
// under that exact name, or nil if none of them was. Never substitutes a different type's
// descriptor.
func (c *capturingMappingClient) selectDescriptor(preferredTypeNames ...string) json.RawMessage {
	for _, preferred := range preferredTypeNames {
		if preferred == "" {
			continue
		}
		for _, ct := range c.captured {
			if strings.EqualFold(ct.name, preferred) {
				return json.RawMessage(ct.json)
			}
		}
	}
	return nil
}

// ── Main ───────────────────────────────────────────────────────────────────────

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(argv []string) int {
	a := parseArgs(argv)

	sc, err := a.require("--scenario")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	if !supportedScenarios[sc] {
		fmt.Fprintf(os.Stderr, "unsupported scenario %q; this driver implements %v\n", sc, supportedScenarios)
		return 2
	}

	phase, err := a.require("--phase")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	tenant, err := a.require("--tenant")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	ownerID, err := a.require("--owner-id")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	idPrefix, err := a.require("--id-prefix")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	outPath, err := a.require("--out")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	grpcAddr, err := a.require("--grpc")
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	typeHint := a.optional("--type")

	clientID := a.optional("--client-id")
	clientSecret := a.optional("--client-secret")
	tokenEndpoint := a.optional("--token-endpoint")
	actingToken := a.optional("--acting-token")

	serviceToken := a.optional("--service-token")

	dialOpts := []grpc.DialOption{grpc.WithInsecure()} //nolint:staticcheck
	// A pre-minted service token wins over the client-credentials trio. Authentik stamps the
	// JWT's `iss` from the request's Host header and grants scopes only when the token request
	// asks for them, so a token this driver minted for itself would be rejected by the API on
	// issuer validation (401) and would carry no schema_admin scope (403 on RegisterSchema) —
	// OAuth2ClientCredentials can set neither. The orchestrator mints one correctly and passes
	// it via --service-token.
	if serviceToken != "" {
		dialOpts = append(dialOpts, grpc.WithPerRPCCredentials(
			staticServiceToken{token: serviceToken, actingToken: actingToken}))
	} else if clientID != "" && clientSecret != "" && tokenEndpoint != "" {
		dialOpts = append(dialOpts, grpc.WithPerRPCCredentials(&iverson.OAuth2ClientCredentials{
			ClientID:      clientID,
			ClientSecret:  clientSecret,
			TokenEndpoint: tokenEndpoint,
		}))
	}

	// The harness normalizes --grpc to `scheme://host:port` (DriverRunner.NormalizeGrpcUrl),
	// because .NET and Java cannot dial without the scheme. grpc.Dial takes a bare `host:port`
	// target and cannot resolve an `http://…` one, so the scheme is stripped back off here.
	dialTarget, err := grpcDialTarget(grpcAddr)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}

	client, err := iverson.NewIversonClient(dialTarget, dialOpts...)
	if err != nil {
		fmt.Fprintf(os.Stderr, "connecting to %s: %v\n", dialTarget, err)
		return 2
	}
	defer client.Close()

	ctx := context.Background()
	if actingToken != "" {
		ctx = iverson.WithActingUserToken(ctx, actingToken)
	}

	priorKeys := parseKeys(a.optional("--keys"), language)
	keyFor := func(logicalName string) string {
		if existing, ok := priorKeys[logicalName]; ok && existing != "" {
			return existing
		}
		return deriveKey(idPrefix, logicalName)
	}

	var steps []stepResult

	switch phase {
	case "register":
		if sc == "naming-rejected" {
			// GoBadArticle's WriterId member fails registrar.buildRequest's naming check before
			// any RegisterSchema call is issued — the capture wrapper never sees a request to
			// record, so there is no typeDescriptor to report either.
			capture := &capturingMappingClient{real: client.MappingStub}
			registrar := iverson.NewSchemaRegistrar(capture, GoBadArticle{})
			regErr := registrar.RegisterAll(ctx, idPrefix, nil)
			step := stepResult{Name: "register", Ok: regErr == nil}
			if regErr != nil {
				msg := regErr.Error()
				step.Error = &msg
			}
			steps = append(steps, step)
			break
		}

		capture := &capturingMappingClient{real: client.MappingStub}
		// Author, then tag, then article — the same order in all five drivers, so the types the
		// article's relations reference already exist when the article is sent. Registration
		// aborts at the first failure, so the order is observable.
		registrar := iverson.NewSchemaRegistrar(capture, GoAuthor{}, GoTag{}, GoArticle{})
		// nil authorization: S1 registers every type WITHOUT an authorization block on purpose,
		// so the orchestrator can re-register it with one and exercise the type both ways.
		regErr := registrar.RegisterAll(ctx, idPrefix, nil)

		addRegisterStep := func(name string, descriptor json.RawMessage) {
			step := stepResult{Name: name, Ok: regErr == nil, TypeDescriptor: descriptor}
			if regErr != nil {
				msg := regErr.Error()
				step.Error = &msg
			}
			steps = append(steps, step)
		}

		addRegisterStep("register", capture.selectDescriptor(typeHint, "GoArticle"))
		addRegisterStep("register_author", capture.selectDescriptor("GoAuthor"))
		addRegisterStep("register_tag", capture.selectDescriptor("GoTag"))

	case "write":
		// Keys are server-assigned: create requests must omit Id entirely, and each row's key is
		// only known — and only reported — once Persist returns it. authorKey/tagKey are filled
		// in by the write_author/write_tag closures below and read by write_article, which runs
		// after them.
		var authorKey, tagKey string

		authorCoord, authorCoordErr := iverson.NewEntityCoordinator(client, GoAuthor{})
		tagCoord, tagCoordErr := iverson.NewEntityCoordinator(client, GoTag{})
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})

		// One step per row: a denied or failed write must not abort the others. A row's key is
		// reported only when its write actually returned one.
		writeStep := func(name, keyName string, do func() (interface{}, string, error)) stepResult {
			var step stepResult
			entity, key, err := do()
			if err != nil {
				step = failStep(name, err)
			} else {
				step = okStep(name)
				step.Entity = entityJSON(entity)
			}
			if key != "" {
				step.Keys = map[string]string{keyName: key}
			}
			return step
		}

		steps = append(steps, writeStep("write_author", "author", func() (interface{}, string, error) {
			if authorCoordErr != nil {
				return nil, "", authorCoordErr
			}
			entity := GoAuthor{TenantId: tenant, OwnerId: ownerID, Name: "author-" + idPrefix}
			key, err := authorCoord.Persist(ctx, entity)
			if err == nil {
				authorKey = key
			}
			return entity, key, err
		}))

		steps = append(steps, writeStep("write_tag", "tag", func() (interface{}, string, error) {
			if tagCoordErr != nil {
				return nil, "", tagCoordErr
			}
			entity := GoTag{TenantId: tenant, OwnerId: ownerID, Label: "tag-" + idPrefix}
			key, err := tagCoord.Persist(ctx, entity)
			if err == nil {
				tagKey = key
			}
			return entity, key, err
		}))

		steps = append(steps, writeStep("write_article", "article", func() (interface{}, string, error) {
			if articleCoordErr != nil {
				return nil, "", articleCoordErr
			}
			entity := GoArticle{
				TenantId: tenant, OwnerId: ownerID, Title: "title-" + idPrefix,
				GoAuthorId: authorKey, GoTagIds: []string{tagKey},
			}
			key, err := articleCoord.Persist(ctx, entity)
			return entity, key, err
		}))

	case "read":
		// Two gets at depth 0 (EntityCoordinator.Get performs no relation traversal), reported
		// separately so a failure on one is not conflated with the other.
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		articleTypeName, articleTypeErr := typeNameOf(GoArticle{})
		if articleCoordErr != nil {
			steps = append(steps, failStep("get", articleCoordErr))
		} else if articleTypeErr != nil {
			steps = append(steps, failStep("get", articleTypeErr))
		} else {
			articleKey := keyFor("article")
			steps = append(steps, reportGet(ctx, client, "get", articleTypeName, articleKey,
				func() (interface{}, error) { return articleCoord.Get(ctx, articleKey) }))
		}

		authorCoord, authorCoordErr := iverson.NewEntityCoordinator(client, GoAuthor{})
		authorTypeName, authorTypeErr := typeNameOf(GoAuthor{})
		if authorCoordErr != nil {
			steps = append(steps, failStep("get_author", authorCoordErr))
		} else if authorTypeErr != nil {
			steps = append(steps, failStep("get_author", authorTypeErr))
		} else {
			authorKey := keyFor("author")
			steps = append(steps, reportGet(ctx, client, "get_author", authorTypeName, authorKey,
				func() (interface{}, error) { return authorCoord.Get(ctx, authorKey) }))
		}

	case "update":
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		if articleCoordErr != nil {
			steps = append(steps, failStep("update", articleCoordErr))
			break
		}
		entity := GoArticle{
			Id: keyFor("article"), TenantId: tenant, OwnerId: ownerID, Title: "title-" + idPrefix + "-updated",
			GoAuthorId: keyFor("author"), GoTagIds: []string{keyFor("tag")},
		}
		// EntityCoordinator.Update returns no entity (unlike .NET's UpdateMappedAsync, which
		// returns the server's response entity) — the entity reported here is what the driver
		// sent, which is the only observable this API surface offers.
		if err := articleCoord.Update(ctx, entity); err != nil {
			steps = append(steps, failStep("update", err))
		} else {
			step := okStep("update")
			step.Entity = entityJSON(entity)
			steps = append(steps, step)
		}

	case "delete":
		articleCoord, articleCoordErr := iverson.NewEntityCoordinator(client, GoArticle{})
		deleteKey := keyFor("article")

		if articleCoordErr != nil {
			steps = append(steps, failStep("delete", articleCoordErr))
			steps = append(steps, failStep("get_after_delete", articleCoordErr))
			break
		}

		if err := articleCoord.Delete(ctx, deleteKey); err != nil {
			steps = append(steps, failStep("delete", err))
		} else {
			steps = append(steps, okStep("delete"))
		}

		// The read-back is its own step, carrying entity (null when nothing came back) and the
		// client's own error text when the read itself fails — a null entity alone cannot
		// distinguish "gone" from "read denied" from a transport error. reportGet keeps that
		// distinction while reporting the not-found case in the same shape as the other four
		// drivers (ok:true, entity null).
		if articleTypeName, articleTypeErr := typeNameOf(GoArticle{}); articleTypeErr != nil {
			steps = append(steps, failStep("get_after_delete", articleTypeErr))
		} else {
			steps = append(steps, reportGet(ctx, client, "get_after_delete", articleTypeName, deleteKey,
				func() (interface{}, error) { return articleCoord.Get(ctx, deleteKey) }))
		}

	default:
		fmt.Fprintf(os.Stderr, "unknown phase %q\n", phase)
		return 2
	}

	document := phaseDocument{Language: language, Phase: phase, Steps: steps}
	out, err := json.Marshal(document)
	if err != nil {
		fmt.Fprintf(os.Stderr, "marshaling phase document: %v\n", err)
		return 2
	}
	if err := os.WriteFile(outPath, out, 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "writing %s: %v\n", outPath, err)
		return 2
	}

	return 0
}
