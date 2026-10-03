/** The few GitHub REST calls the Worker makes: open an issue, read one back. */
export class GitHubClient {
  constructor(private readonly repo: string, private readonly token: string) {}

  private async call(path: string, init: RequestInit = {}): Promise<Response> {
    const response = await fetch(`https://api.github.com${path}`, {
      ...init,
      headers: {
        Accept: "application/vnd.github+json",
        Authorization: `Bearer ${this.token}`,
        "User-Agent": "PlayniteAchievements-Workshop-Worker",
        "X-GitHub-Api-Version": "2022-11-28",
        ...(init.headers ?? {}),
      },
    });
    if (!response.ok) {
      throw new Error(`GitHub ${init.method ?? "GET"} ${path} failed: ${response.status} ${await response.text()}`);
    }
    return response;
  }

  async createIssue(title: string, body: string, labels: string[]): Promise<{ number: number; html_url: string }> {
    const response = await this.call(`/repos/${this.repo}/issues`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ title, body, labels }),
    });
    return (await response.json()) as { number: number; html_url: string };
  }

  async getIssue(number: number): Promise<Issue> {
    const response = await this.call(`/repos/${this.repo}/issues/${number}`);
    return (await response.json()) as Issue;
  }

  async getLastBotComment(number: number): Promise<string | null> {
    const response = await this.call(`/repos/${this.repo}/issues/${number}/comments?per_page=100`);
    const comments = (await response.json()) as { body: string; user: { login: string; type: string } }[];
    for (let i = comments.length - 1; i >= 0; i--) {
      if (comments[i].user.type === "Bot" || comments[i].user.login.endsWith("[bot]")) {
        return comments[i].body;
      }
    }
    return null;
  }
}

export interface Issue {
  number: number;
  html_url: string;
  state: "open" | "closed";
  state_reason?: "completed" | "not_planned" | "reopened" | null;
  labels: { name: string }[];
  pull_request?: unknown;
}
