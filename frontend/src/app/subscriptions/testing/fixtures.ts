import detailJson from './detail.json';
import optionsJson from './options.json';
import overviewJson from './overview.json';
import validationProblemJson from './validation-problem.json';
import {
  ApiProblem,
  SubscriptionDetail,
  SubscriptionOptions,
  SubscriptionOverview,
  Wire,
} from '../subscription.models';

// The golden files are generated from the live API responses and compared with them by the API tests. Each is also checked
// against its DTO interface at compile time: a plain typed import does not compile (a JSON import's strings are plain `string`s,
// which the literal unions reject), and a bare `as` would compile even when a required property is missing.
const overviewCheck: Wire<SubscriptionOverview> = overviewJson;
const detailCheck: Wire<SubscriptionDetail> = detailJson;
const optionsCheck: Wire<SubscriptionOptions> = optionsJson;
const problemCheck: Wire<ApiProblem> = validationProblemJson;
void [overviewCheck, detailCheck, optionsCheck, problemCheck];

const copy = <T>(value: unknown): T => structuredClone(value) as T;

export const overviewFixture = (): SubscriptionOverview => copy(overviewJson);
export const detailFixture = (): SubscriptionDetail => copy(detailJson);
export const optionsFixture = (): SubscriptionOptions => copy(optionsJson);
export const validationProblemFixture = (): ApiProblem => copy(validationProblemJson);
