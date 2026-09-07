import { render, screen, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { App } from "./App";
import { server } from "./test/server";

describe("App", () => {
  it("keeps workspace controls together and release metadata compact", async () => {
    // GIVEN an authenticated workspace with a full build identifier.
    server.use(
      http.get("*/api/system", () =>
        HttpResponse.json({
          name: "Workbench",
          version: "1.2.3+abcdef0123456789",
        }),
      ),
      http.get("*/api/auth/me", () =>
        HttpResponse.json({
          userId: "11111111-1111-1111-1111-111111111111",
          email: "admin@example.com",
          tenantName: "Tenant A",
          permissions: ["TenantAccess"],
        }),
      ),
      http.get("*/api/items", () =>
        HttpResponse.json({ items: [], nextCursor: null }),
      ),
    );

    // WHEN the collection shell loads.
    render(<App />);

    expect(screen.getByRole("status")).toHaveTextContent("Loading");
    expect(
      await screen.findByRole("heading", { name: "Collection" }),
    ).toBeVisible();
    // THEN appearance and sign-out remain together in the workspace header.
    const header = within(screen.getByRole("banner"));
    expect(header.getByRole("combobox", { name: "Appearance" })).toBeVisible();
    expect(header.getByRole("button", { name: "Sign out" })).toBeVisible();
    expect(
      screen.getAllByRole("combobox", { name: "Appearance" }),
    ).toHaveLength(1);
    // AND the release label stays compact while retaining the full build as metadata.
    expect(screen.getByText("Workbench 1.2.3")).toHaveAttribute(
      "title",
      "1.2.3+abcdef0123456789",
    );
  });

  it("renders a safe failure state", async () => {
    server.use(
      http.get("*/api/system", () =>
        HttpResponse.json(
          {
            type: "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1",
            title: "An unexpected error occurred.",
            status: 500,
            traceId: "test-trace",
          },
          { status: 500 },
        ),
      ),
      http.get("*/api/auth/me", () => new HttpResponse(null, { status: 401 })),
    );

    render(<App />);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Workbench is temporarily unavailable.",
    );
    expect(
      screen.queryByText("An unexpected error occurred."),
    ).not.toBeInTheDocument();
  });
});
