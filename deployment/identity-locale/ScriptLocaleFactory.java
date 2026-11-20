package io.claimcore.evaluation;

import java.util.Arrays;
import java.util.Locale;
import java.util.Optional;
import java.util.stream.Stream;

import jakarta.enterprise.context.ContextNotActiveException;
import org.keycloak.cookie.CookieProvider;
import org.keycloak.cookie.CookieType;
import org.keycloak.locale.DefaultLocaleSelectorProvider;
import org.keycloak.locale.DefaultLocaleSelectorProviderFactory;
import org.keycloak.locale.LocaleSelectorProvider;
import org.keycloak.models.KeycloakSession;
import org.keycloak.models.RealmModel;
import org.keycloak.models.UserModel;

public final class ScriptLocaleFactory extends DefaultLocaleSelectorProviderFactory {
    @Override
    public String getId() {
        return "claimcore-script";
    }

    @Override
    public LocaleSelectorProvider create(KeycloakSession session) {
        return new DefaultLocaleSelectorProvider(session) {
            @Override
            public Locale resolveLocale(RealmModel realm, UserModel user, boolean ignoreAcceptLanguage) {
                var selected = super.resolveLocale(realm, user, ignoreAcceptLanguage);
                if (!selected.getLanguage().equals("zh")) {
                    return selected;
                }

                // Keycloak 26.8.2 compares language, country and variant, but omits script.
                // Retain its selected language and recover the first Chinese tag in its precedence order.
                var authentication = session.getContext().getAuthenticationSession();
                var explicit = authentication == null
                        ? session.getAttribute(USER_REQUEST_LOCALE, String.class)
                        : authentication.getAuthNote(USER_REQUEST_LOCALE);
                var profile = user == null ? null : user.getFirstAttribute(UserModel.LOCALE);
                var hint = authentication == null ? null : authentication.getClientNote(CLIENT_REQUEST_LOCALE);

                var preference = Stream.concat(
                        Stream.of(explicit, profile),
                        hint == null ? Stream.empty() : Arrays.stream(hint.split(" ")));
                var candidate = firstChinese(preference)
                        .or(() -> firstChinese(browserPreferences(session, ignoreAcceptLanguage)));

                return candidate.filter(locale -> !locale.getScript().isEmpty()).flatMap(requested ->
                        realm.getSupportedLocalesStream()
                                .map(Locale::forLanguageTag)
                                .filter(locale -> locale.getLanguage().equals(requested.getLanguage())
                                        && locale.getScript().equals(requested.getScript()))
                                .findFirst()).orElse(selected);
            }
        };
    }

    private static Optional<Locale> firstChinese(Stream<String> preferences) {
        return preferences.filter(tag -> tag != null)
                .map(Locale::forLanguageTag)
                .filter(locale -> locale.getLanguage().equals("zh"))
                .findFirst();
    }

    private static Stream<String> browserPreferences(KeycloakSession session, boolean ignoreAcceptLanguage) {
        try {
            var headers = session.getContext().getRequestHeaders();
            if (headers == null) {
                return Stream.empty();
            }
            var cookie = session.getProvider(CookieProvider.class).get(CookieType.LOCALE);
            var languages = ignoreAcceptLanguage ? null : headers.getAcceptableLanguages();
            return Stream.concat(Stream.of(cookie),
                    languages == null ? Stream.empty() : languages.stream().map(Locale::toLanguageTag));
        } catch (ContextNotActiveException ignored) {
            return Stream.empty();
        }
    }
}
