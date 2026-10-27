// Round 2, measured locally (s3-l16): five scenarios at once against one web task and its workers
// (scripts/load-local.sh <duration> round2). Every result is a local measurement, printed with its setup.
//   posting       - people post (writes, fanout behind them)
//   feed          - people refresh their feeds
//   celebrity     - everyone follows one pulled author, who posts, and refreshes (the read storm)
//   returning     - people who never read before ask for page 1 (no timeline: the two-stage rebuild)
//   who_to_follow - people ask for suggestions
import http from 'k6/http';
import { check } from 'k6';
import { SharedArray } from 'k6/data';
import exec from 'k6/execution';

const base = __ENV.BASE;
const people = new SharedArray('people', () => JSON.parse(__ENV.PEOPLE));
const duration = __ENV.DURATION || '60s';
// people[0] is the pulled author (the script puts it in the registry); the last third are the returners,
// who read for the first time during the run, one each.
const returners = Math.floor(people.length / 3);
const regulars = people.length - returners;

export const options = {
  scenarios: {
    posting: { executor: 'constant-arrival-rate', rate: 20, timeUnit: '1s', duration, preAllocatedVUs: 20, exec: 'post' },
    feed: { executor: 'constant-vus', vus: 20, duration, exec: 'feed' },
    celebrity: { executor: 'constant-vus', vus: 20, duration, exec: 'celebrity' },
    returning: { executor: 'shared-iterations', vus: 5, iterations: returners, maxDuration: duration, exec: 'returning' },
    who_to_follow: { executor: 'constant-arrival-rate', rate: 10, timeUnit: '1s', duration, preAllocatedVUs: 10, exec: 'whoToFollow' },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    'http_req_duration{scenario:feed}': ['p(99)<1000'],
    'http_req_duration{scenario:celebrity}': ['p(99)<1000'],
    'http_req_duration{scenario:returning}': ['p(99)<1000'],
    'http_req_duration{scenario:posting}': ['p(99)<1000'],
    'http_req_duration{scenario:who_to_follow}': ['p(99)<2000'],
  },
};

const headers = (p) => ({ Authorization: `Bearer ${p.token}`, 'Content-Type': 'application/json' });
const regular = () => people[1 + Math.floor(Math.random() * (regulars - 1))];

export function setup() {
  for (const p of people) {
    const r = http.put(`${base}/v1/me`, JSON.stringify({ handle: p.handle, display_name: p.handle }), { headers: headers(p) });
    check(r, { registered: (x) => x.status === 200 || x.status === 201 });
  }
  // Everyone follows the next ten people and the pulled author.
  for (let i = 1; i < people.length; i++) {
    http.put(`${base}/v1/accounts/${people[0].id}/follow`, null, { headers: headers(people[i]) });
    for (let j = 1; j <= 10; j++) {
      const other = people[1 + ((i + j) % (people.length - 1))];
      if (other.id !== people[i].id) {
        http.put(`${base}/v1/accounts/${other.id}/follow`, null, { headers: headers(people[i]) });
      }
    }
  }
  for (let i = 0; i < 30; i++) {
    http.post(`${base}/v1/posts`, JSON.stringify({ text: `from the star ${i}` }),
      { headers: { ...headers(people[0]), 'Idempotency-Key': `star-${i}-${Math.random()}` } });
  }
}

export function post() {
  const p = regular();
  const key = `${exec.vu.idInTest}-${exec.scenario.iterationInTest}-${Math.random()}`;
  const r = http.post(`${base}/v1/posts`, JSON.stringify({ text: `round 2 ${key}` }), { headers: { ...headers(p), 'Idempotency-Key': key } });
  check(r, { stored: (x) => x.status === 201 });
}

export function feed() {
  const r = http.get(`${base}/v1/feed?limit=20`, { headers: headers(regular()) });
  check(r, { page: (x) => x.status === 200 });
}

export function celebrity() {
  const r = http.get(`${base}/v1/feed?limit=20`, { headers: headers(regular()) });
  check(r, { page: (x) => x.status === 200 });
}

export function returning() {
  const p = people[regulars + exec.scenario.iterationInTest];
  const r = http.get(`${base}/v1/feed?limit=20`, { headers: headers(p) });
  check(r, { first_page: (x) => x.status === 200 && x.json('items').length > 0 });
}

export function whoToFollow() {
  const r = http.get(`${base}/v1/suggestions/who-to-follow`, { headers: headers(regular()) });
  check(r, { suggestions: (x) => x.status === 200 });
}
