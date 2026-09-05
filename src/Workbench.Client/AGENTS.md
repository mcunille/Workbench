# Client guidance

These rules supplement the root guidance, including TDD, mutation testing, and Gherkin comments.

- Colocate tests with the modules they cover. Unit-test pure transformations, validation, formatting, and form reconciliation directly. Use component tests for user-visible behavior that depends on components being wired together; avoid duplicating helper tests through a component.
- In component tests, mock the feature API boundary when one exists. Test transport, serialization, and error handling separately at the API-client boundary. Use HTTP or end-to-end coverage when the behavior being verified crosses that boundary.
- Describe conditions, actions, and outcomes with Gherkin comments as well as descriptive test names. Assert observable behavior and accessible interactions rather than component implementation details.
- Keep the client on the generated API contract; do not duplicate server business rules as an alternative authority. Follow the existing design system and inspect affected workflows in the running browser, including relevant loading, empty, error, validation, keyboard, and responsive states.
