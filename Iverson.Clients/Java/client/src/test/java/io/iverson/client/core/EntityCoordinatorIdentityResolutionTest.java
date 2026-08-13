package io.iverson.client.core;

import io.iverson.client.annotations.IversonEntity;
import io.iverson.client.annotations.IversonKey;
import iverson.ObjectMapping;
import iverson.ObjectMappingServiceGrpc;
import iverson.ObjectPersistence;
import iverson.ObjectRetrieval;
import iverson.ObjectSearch;
import iverson.ObjectPersistenceServiceGrpc;
import iverson.ObjectRetrievalServiceGrpc;
import iverson.ObjectSearchServiceGrpc;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.extension.ExtendWith;
import org.mockito.Mock;
import org.mockito.junit.jupiter.MockitoExtension;

import java.util.UUID;

import static org.mockito.ArgumentMatchers.any;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.ArgumentMatchers.eq;
import static org.mockito.Mockito.lenient;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;

/**
 * Tests the four-level acting-user identity resolution rule (explicit per-call token, then
 * coordinator-bound via {@link EntityCoordinator#withActingUser(String)}, then the client's
 * ambient token, then none) across multiple stub families — not just search — since Java
 * attaches identity per stub family via {@code EntityCoordinator.withIdentity}.
 *
 * <p>All assertions are on the call option actually applied to the mocked stub
 * ({@code verify(stub).withOption(...)} / {@code verify(stub, never()).withOption(...)}), never
 * on a client-side field, mirroring {@code EntityCoordinatorTest}.
 */
@ExtendWith(MockitoExtension.class)
class EntityCoordinatorIdentityResolutionTest {

    @IversonEntity
    static class IdentityTestArticle {
        @IversonKey
        private UUID id;
        private String title;
    }

    @Mock
    private ObjectPersistenceServiceGrpc.ObjectPersistenceServiceBlockingStub mockPersistenceStub;

    @Mock
    private ObjectRetrievalServiceGrpc.ObjectRetrievalServiceBlockingStub mockRetrievalStub;

    @Mock
    private ObjectMappingServiceGrpc.ObjectMappingServiceBlockingStub mockMappingStub;

    @Mock
    private ObjectSearchServiceGrpc.ObjectSearchServiceBlockingStub mockSearchStub;

    @BeforeEach
    void setUp() {
        // lenient: the rule-4 (no token anywhere) case never exercises withOption, exactly as
        // EntityCoordinatorTest.java:57-58 declares for the same reason under STRICT_STUBS.
        lenient().when(mockPersistenceStub.withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockPersistenceStub);
        lenient().when(mockRetrievalStub.withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockRetrievalStub);
        lenient().when(mockMappingStub.withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockMappingStub);
        lenient().when(mockSearchStub.withOption(eq(OAuth2ClientCredentials.ACTING_USER_TOKEN), anyString()))
            .thenReturn(mockSearchStub);
    }

    private EntityCoordinator<IdentityTestArticle> coordinatorWithAmbient(String ambientToken) {
        IversonClient client = new IversonClient(
            mockPersistenceStub, mockRetrievalStub, mockMappingStub, mockSearchStub, ambientToken);
        return new EntityCoordinator<>(client, IdentityTestArticle.class);
    }

    // ── Rule 4: no token anywhere → no call option attached ────────────────────

    @Test
    void noTokenAnywhere_attachesNoCallOption_persist() {
        when(mockPersistenceStub.post(any())).thenReturn(
            ObjectPersistence.PersistResponse.newBuilder().setSuccess(true).setKey("k1").build());

        EntityCoordinator<IdentityTestArticle> sut = coordinatorWithAmbient(null);
        sut.persist(new IdentityTestArticle());

        verify(mockPersistenceStub, never()).withOption(any(), any());
    }

    @Test
    void noTokenAnywhere_attachesNoCallOption_get() {
        when(mockRetrievalStub.get(any())).thenReturn(
            ObjectRetrieval.RetrievalResponse.newBuilder().setFound(false).build());

        EntityCoordinator<IdentityTestArticle> sut = coordinatorWithAmbient(null);
        sut.get("some-id");

        verify(mockRetrievalStub, never()).withOption(any(), any());
    }

    @Test
    void noTokenAnywhere_attachesNoCallOption_delete() {
        when(mockMappingStub.delete(any())).thenReturn(
            ObjectMapping.MappingDeleteResponse.newBuilder().setSuccess(true).build());

        EntityCoordinator<IdentityTestArticle> sut = coordinatorWithAmbient(null);
        sut.delete("some-id");

        verify(mockMappingStub, never()).withOption(any(), any());
    }

    // ── Rule 3: ambient client default applies when nothing is bound and no explicit token ──

    @Test
    void theAmbientIdentityAppliesWhenNothingIsBound() {
        when(mockPersistenceStub.post(any())).thenReturn(
            ObjectPersistence.PersistResponse.newBuilder().setSuccess(true).setKey("k1").build());

        EntityCoordinator<IdentityTestArticle> sut = coordinatorWithAmbient("ambient-token");
        sut.persist(new IdentityTestArticle());

        verify(mockPersistenceStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "ambient-token");
    }

    @Test
    void ambientIdentity_appliesToGet_whenNothingIsBound() {
        when(mockRetrievalStub.get(any())).thenReturn(
            ObjectRetrieval.RetrievalResponse.newBuilder().setFound(false).build());

        EntityCoordinator<IdentityTestArticle> sut = coordinatorWithAmbient("ambient-token");
        sut.get("some-id");

        verify(mockRetrievalStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "ambient-token");
    }

    // ── Rule 2: coordinator-bound token (withActingUser) takes precedence over ambient ─────

    @Test
    void boundIdentity_takesPrecedenceOverAmbient() {
        when(mockPersistenceStub.post(any())).thenReturn(
            ObjectPersistence.PersistResponse.newBuilder().setSuccess(true).setKey("k1").build());

        EntityCoordinator<IdentityTestArticle> sut =
            coordinatorWithAmbient("ambient-token").withActingUser("bound");
        sut.persist(new IdentityTestArticle());

        verify(mockPersistenceStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "bound");
    }

    @Test
    void boundIdentity_appliesToSearch() {
        when(mockSearchStub.search(any())).thenReturn(
            java.util.List.<ObjectSearch.SearchResponse>of().iterator());

        EntityCoordinator<IdentityTestArticle> sut =
            coordinatorWithAmbient(null).withActingUser("bound");
        sut.search(io.iverson.client.search.Query.of(IdentityTestArticle.class));

        verify(mockSearchStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "bound");
    }

    @Test
    void withActingUser_doesNotMutateOriginalCoordinator() {
        when(mockPersistenceStub.post(any())).thenReturn(
            ObjectPersistence.PersistResponse.newBuilder().setSuccess(true).setKey("k1").build());

        EntityCoordinator<IdentityTestArticle> original = coordinatorWithAmbient(null);
        EntityCoordinator<IdentityTestArticle> bound = original.withActingUser("bound");

        original.persist(new IdentityTestArticle());

        verify(mockPersistenceStub, never()).withOption(any(), any());
    }

    // ── Rule 1: explicit per-call token takes precedence over bound and ambient ────────────

    @Test
    void explicitToken_takesPrecedenceOverBoundAndAmbient() {
        when(mockMappingStub.get(any())).thenReturn(
            ObjectMapping.MappingResponse.newBuilder().setSuccess(false).build());

        EntityCoordinator<IdentityTestArticle> sut =
            coordinatorWithAmbient("ambient-token").withActingUser("bound");
        sut.getMapped("some-id", 1, "explicit");

        verify(mockMappingStub).withOption(OAuth2ClientCredentials.ACTING_USER_TOKEN, "explicit");
    }
}
