import DocumentationPage from '@/components/DocumentationPage';
import { createDocumentationStaticProps } from '@/lib/documentation';

export const getStaticProps = createDocumentationStaticProps('dead-letter-queues');

export default DocumentationPage;
